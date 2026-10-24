import os
import re
import argparse

def unpack_record(record):
    term = ""
    text = ""
    try:
        (term, text) = record.split("=", 1)
        text = text.strip()
    except:
        term = record

    return term, text if text != "" else "EMPTY"

def upper_repl(match):
     return match.group(1) + "=" + match.group(2).upper() + match.group(3)

def main():
    parser = argparse.ArgumentParser(description='Sort and normalize localization text files')
    parser.add_argument('folder', nargs='?', default='.', help='folder to process recursively (default: current folder)')
    args = parser.parse_args()
    for root, dirs, files in os.walk(args.folder):
        for file in files:
            if file.endswith('.txt'):
                filename = os.path.join(root, file)
                print(f"sorting {filename}")
                with open(filename, "rt", encoding="utf-8") as f:
                    data = f.readlines()
                    if data:
                        data[0] = data[0].replace('﻿', '')
                    for idx, record in enumerate(data):
                        data[idx] = re.sub(r"(.+?)=(.)(.*)", upper_repl, data[idx])
                    data.sort()
                with open(filename, "wt", encoding="utf-8") as f:
                    f.writelines(data)


if __name__ == '__main__':
    main()
