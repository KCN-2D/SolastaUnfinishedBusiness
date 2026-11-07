"""Inspect representative compiled passes without launching a graphics process."""
import argparse
import ctypes
import json
from pathlib import Path
import re
import struct
import UnityPy
from UnityPy.export.ShaderConverter import ShaderProgram
from UnityPy.helpers import CompressionHelper
from UnityPy.streams import EndianBinaryReader


def disassemble(code):
    start = code.index(b'DXBC')
    length = struct.unpack_from('<I', code, start + 24)[0]
    assert 32 <= length <= len(code) - start
    binary = ctypes.create_string_buffer(code[start:start + length])
    blob = ctypes.c_void_p()
    api = ctypes.WinDLL('d3dcompiler_47.dll').D3DDisassemble
    api.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint, ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
    api.restype = ctypes.c_long
    status = api(binary, length, 0, None, ctypes.byref(blob))
    assert status >= 0 and blob.value
    table = ctypes.cast(blob, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents

    def call(result, index):
        return ctypes.WINFUNCTYPE(result, ctypes.c_void_p)(table[index])(blob)

    try:
        return ctypes.string_at(call(ctypes.c_void_p, 3), call(ctypes.c_size_t, 4)).decode('ascii').rstrip('\0')
    finally:
        call(ctypes.c_ulong, 2)


def inspect(path, output, prefix):
    environment = UnityPy.load(str(path))
    reader = next(obj for obj in environment.objects if obj.type.name == 'Shader')
    shader, tree = reader.read(), reader.read_typetree()
    first = lambda x: x[0][0] if isinstance(x[0], list) else x[0]
    compressed = bytes(shader.compressedBlob)[first(shader.offsets):first(shader.offsets)+first(shader.compressedLengths)]
    raw = CompressionHelper.decompress_lz4(compressed, first(shader.decompressedLengths))
    program = ShaderProgram(EndianBinaryReader(raw, endian='<'), reader.version)
    result = []
    for index, shader_pass in enumerate(tree['m_ParsedForm']['m_SubShaders'][0]['m_Passes']):
        declaration = shader_pass['progFragment']['m_SubPrograms'][0]
        names = {number: name for name, number in shader_pass['m_NameIndices']}
        code = bytes(program.m_SubPrograms[declaration['m_BlobIndex']].m_ProgramCode)
        assembly = disassemble(code)
        destination = output / (prefix + '-Pass' + str(index) + '.asm')
        destination.write_text(assembly, encoding='ascii')
        textures = [names[item['m_NameIndex']] for item in declaration['m_TextureParams']]
        bindings = {item['m_NameIndex']: item['m_Index'] for item in declaration['m_ConstantBufferBindings']}
        constants = [dict(name=names[item['m_NameIndex']], buffer=bindings[buffer['m_NameIndex']],
                          byteOffset=item['m_Index'], dimension=item['m_Dim'])
                     for buffer in declaration['m_ConstantBuffers'] for item in buffer['m_VectorParams']]
        count = len(re.findall(r'^\s*(?:\d+: )?(?!//)(?:add|mul|mad|div|mov|dp[234]|sqrt|rsq|sample\w*|if\w*|endif|ret|lt|ge|eq|ne|min|max|log|exp|frc|deriv\w*)\b', assembly, re.M))
        state=shader.m_ParsedForm.m_SubShaders[0].m_Passes[index].m_State
        result.append({'pass': index, 'name':state.m_Name, 'lightMode':dict(state.m_Tags.tags).get('LIGHTMODE'), 'textures': textures, 'textureCount': len(textures), 'constants': constants,
                       'selectedArithmeticAndFlowInstructions': count, 'assembly': str(destination)})
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--candidate', required=True, type=Path)
    parser.add_argument('--baseline', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True)
    before = inspect(args.baseline, args.output, 'Baseline')
    after = inspect(args.candidate, args.output, 'Candidate')
    assert [(p['name'],p['lightMode'])for p in before]==[(p['name'],p['lightMode'])for p in after]
    assert {'FORWARDBASE','FORWARDADD','SHADOWCASTER'}<={p['lightMode']for p in before}
    result = {'status': 'PASS_COMPILED_PASS_REVIEW', 'scope': 'First compiled DXBC fragment variant in each actual baseline-matched pass; instruction counts are static observations, not GPU timings.', 'baseline': before, 'candidate': after}
    (args.output / 'CompiledPassReview.json').write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
