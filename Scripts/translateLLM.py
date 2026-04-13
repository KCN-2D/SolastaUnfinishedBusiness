#!/usr/bin/env python3
#
# AUTHOR: Translation Script using LLM
#
# DESCRIPTION: This script translates localization files from one language to all others
#              using an OpenAI-compatible API with concurrent translation support.
#
# REQUIRES:
#   - Python 3.9+
#   - openai library (pip install openai)
#   - python-dotenv library (pip install python-dotenv)
#   - rich library (pip install rich)
#
# USAGE:
#   python translateLLM.py <input_path> [--force] [--target LANG]
#   
#   Examples:
#     # Translate all files in a language folder (from Scripts directory)
#     python translateLLM.py en
#     
#     # Translate a single file (from Scripts directory)
#     python translateLLM.py en/Backgrounds-en.txt
#     
#     # Force retranslation of all entries (including existing ones)
#     python translateLLM.py en --force
#     
#     # Translate to specific target language(s) only
#     python translateLLM.py zh-CN --target en
#     python translateLLM.py zh-CN --target en,de,fr

import os
import sys
import codecs
import time
import argparse
import csv
from pathlib import Path
from typing import Dict, List, Tuple, Optional, Set
from dataclasses import dataclass, field
from queue import Queue
from concurrent.futures import ThreadPoolExecutor, as_completed
from threading import Lock
from collections import defaultdict
from dotenv import load_dotenv
from openai import OpenAI
from rich.console import Console
from rich.progress import Progress, TaskID, BarColumn, TextColumn, TimeRemainingColumn, SpinnerColumn
from rich.panel import Panel

# Load environment variables from .env file
load_dotenv(Path(__file__).parent / '.env')

# Configuration from environment variables
API_BASE_URL = os.getenv('API_BASE_URL', 'https://api.openai.com/v1')
API_KEY = os.getenv('API_KEY')
MODEL_ID = os.getenv('MODEL_ID', 'gpt-4')
BATCH_SIZE = int(os.getenv('BATCH_SIZE', '10'))
MAX_RETRIES = int(os.getenv('MAX_RETRIES', '3'))
RETRY_DELAY = int(os.getenv('RETRY_DELAY', '2'))
MAX_WORKERS = int(os.getenv('MAX_WORKERS', '3'))
GLOSSARY_PATH = Path(__file__).parent / 'ja_glossary.tsv'

# Validate configuration
if not API_KEY:
    print("ERROR: API_KEY not found in .env file. Please copy .env.example to .env and configure it.")
    sys.exit(1)

# Initialize OpenAI client
client = OpenAI(
    api_key=API_KEY,
    base_url=API_BASE_URL
)

# Console for rich output
console = Console()

# Language code mapping
LANGUAGE_NAMES = {
    'zh-CN': 'Simplified Chinese',
    'en': 'English',
    'de': 'German',
    'es': 'Spanish',
    'fr': 'French',
    'it': 'Italian',
    'ja': 'Japanese',
    'ko': 'Korean',
    'pt-BR': 'Brazilian Portuguese',
    'ru': 'Russian'
}


def load_ja_glossary() -> List[Dict[str, str]]:
    """Load the Japanese terminology glossary used for prompt steering."""
    if not GLOSSARY_PATH.exists():
        return []

    with open(GLOSSARY_PATH, 'r', encoding='utf-8', newline='') as handle:
        reader = csv.DictReader(handle, delimiter='\t')
        return list(reader)


def select_relevant_ja_glossary(
    entries: List[Tuple[str, str]],
    glossary: List[Dict[str, str]],
    limit: int = 48
) -> List[Dict[str, str]]:
    """Pick glossary rows that are relevant to the current translation batch."""
    if not glossary:
        return []

    batch_text = "\n".join(f"{key}={text}" for key, text in entries)
    batch_text_lower = batch_text.lower()
    always_include = {
        "system",
        "ability",
        "damage_type",
        "skill",
        "weapon",
        "armor",
        "item",
        "creature",
        "creature_type",
        "ui",
        "class_feature",
        "proper_noun",
    }
    scored_rows: List[Tuple[int, str, Dict[str, str]]] = []

    for row in glossary:
        source = row["source"].strip()
        preferred = row["preferred_ja"].strip()
        alt_text = row["alt_ja"].strip()
        category = row["category"].strip()
        alts = [part.strip() for part in alt_text.split("|") if part.strip()]

        score = 0
        if category in always_include:
            score += 2

        if source and source.lower() in batch_text_lower:
            score += 8

        if preferred and preferred in batch_text:
            score += 4

        for alt in alts:
            if alt and alt in batch_text:
                score += 6
                break

        if category == "feat" and ("Feat/&" in batch_text or "特技" in batch_text):
            score += 2
        elif category == "spell" and ("Spell/&" in batch_text or "Cantrip" in batch_text or "初級呪文" in batch_text):
            score += 2
        elif category == "skill" and "Skill/&" in batch_text:
            score += 2
        elif category in {"weapon", "armor", "item"} and ("Item/&" in batch_text or "Equipment/&" in batch_text):
            score += 2
        elif category in {"creature", "creature_type"} and (
            "Monster/&" in batch_text or "CharacterFamily/&" in batch_text or "Language/&" in batch_text
        ):
            score += 2
        elif category == "proper_noun" and (
            "Item/&" in batch_text
            or "Equipment/&" in batch_text
            or "Monster/&" in batch_text
            or "CharacterFamily/&" in batch_text
            or "Narration/&" in batch_text
            or "Quest/&" in batch_text
            or "Faction/&" in batch_text
            or "Location/&" in batch_text
        ):
            score += 2
        elif category in {"class_feature", "ui"} and (
            "Feature/&" in batch_text or "Screen/&" in batch_text or "UI/&" in batch_text or "ModUi/&" in batch_text
        ):
            score += 2
        elif source == "Eldritch Invocation" and ("Invocation" in batch_text or "妖術" in batch_text):
            score += 4

        if score > 0:
            scored_rows.append((score, source.lower(), row))

    scored_rows.sort(key=lambda item: (-item[0], item[1]))
    return [row for _, _, row in scored_rows[:limit]]


def build_target_language_guidance(
    target_lang_code: str,
    entries: List[Tuple[str, str]],
) -> Tuple[str, str]:
    """Build extra prompt guidance for a specific target language."""
    if target_lang_code != 'ja':
        return "", ""

    glossary = load_ja_glossary()
    glossary = select_relevant_ja_glossary(entries, glossary)
    glossary_lines = []
    for row in glossary:
        source = row['source'].strip()
        preferred = row['preferred_ja'].strip()
        alt = row['alt_ja'].strip()
        suffix = f" (avoid: {alt})" if alt else ""
        glossary_lines.append(f"- {source} => {preferred}{suffix}")

    system_guidance = (
        "When the target language is Japanese, use official D&D 5e/5.5e terminology whenever available. "
        "Prefer glossary-approved wording, keep keyword spelling consistent, distinguish flavorful text from "
        "mechanical rules text, and prioritize system accuracy over style when the text is rule-bearing."
    )

    user_guidance = f"""

Japanese terminology guidance:
- Use official tabletop D&D Japanese terms whenever possible.
- Treat Scripts/ja_glossary.tsv as the repo-local authority derived from dnd5eja's archive/DnD_Glossary_JP.txt first and lang/ja.json second.
- If dnd5eja's archive/DnD_Glossary_JP.txt and lang/ja.json disagree, use the archive wording.
- Prefer kanji-based official renderings in system text when the official glossary lists both katakana and kanji.
- When the official glossary lists a katakana/kanji pair separated by a slash, prefer the Japanese-side rendering in system text.
- If the official glossary only gives a katakana rendering for a weapon, armor, creature, or item noun, keep that katakana form instead of inventing a kanji synonym.
- If a proper noun is not in the official glossary, do not leave it in English. Transliterate it into consistent katakana and reuse the same spelling everywhere in the repo.
- Translate Cantrip as 初級呪文.
- Translate Wild Shape as 自然の化身.
- Translate Zephyr Strike as 微風の打撃 in system text.
- Translate Telekinesis as 念動力 in system text.
- Translate Elemental Weapon as 元素武器 in system text.
- Translate Counterspell as 呪文妨害 in system text.
- Translate Dispel Magic as 魔法解呪 in system text.
- Translate Aganazzar's Scorcher as アガナザーの火炎放射 in system text.
- Translate Hold Person as 対人金縛り and Hold Monster as 怪物金縛り in system text.
- Translate Disintegrate as 分解, Dispel Evil and Good as 善悪解呪, Dominate Person as 人物支配, and Dominate Monster as 怪物支配 in system text.
- Translate Create or Destroy Water as 水の生成・破壊, Divine Word as 神言, Earthquake as 地震, Darkness as 暗闇, Contagion as 感染, and Cone of Cold as 冷気噴射 in system text.
- Translate Abi-Dalzim's Horrid Wilting as アビー・ダルジムの恐るべき枯渇 in system text.
- Translate See Invisibility as 不可視視認, Searing Smite as 灼熱の一撃, Call Lightning as 招雷, Cloudkill as 殺戮の雲, Command as 命令, Blindness/Deafness as 視覚・聴覚剥奪, Calm Emotions as 感情鎮静化, and Blur as かすみ in system text.
- Translate Vicious Mockery as 悪意ある嘲り, Raise Dead as 死者の復活, Sunbeam as 陽光, Sunburst as 陽光爆発, Phantasmal Killer as 幻の殺し屋, Pass without Trace as 跡を残さぬ移動, Flame Blade as 炎の刃, True Seeing as 真実の目, Tongues as 言語会話, Hypnotic Pattern as 催眠文様, Ice Storm as 氷の嵐, Holy Aura as 聖なるオーラ, Spiritual Weapon as 心霊武器, Spike Growth as トゲ密生, Gust of Wind as 強風, Guiding Bolt as 導きの矢, Eyebite as 魔眼, Death Ward as 死からの守り, Create Food and Water as 食糧と水の創造, Dominate Beast as 野獣支配, Dreadful Omen as 凶兆, Feeblemind as 知能低下, Finger of Death as 死神の指, Hunter's Mark as 狩人の印, Maze as 迷路, Wall of Thorns as イバラの壁, Huge as 超大型, Gargantuan as 巨大, Foe Slayer as 仇敵殺し, Polearm Master as 長柄の使い手, and Heavy Armor Master as 重装鎧の達人 in system text.
- Translate Moonbeam as 月光, Wind Wall as 風の壁, Circle of Death as 死の円環, Shield of Faith as 信仰の盾, Evard's Black Tentacles as エヴァードの黒い触手, Absorb Elements as 元素吸収, Synaptic Static as 脳神経抑圧, Giant Insect as 蟲類巨大化, Otiluke's Freezing Sphere as オティルークの冷凍球, Alarm as 警報, Chaos Bolt as 混沌の矢, Divine Favor as 神寵, and Tasha's Caustic Brew as ターシャのコースティック・ブリュー in system text.
- Translate Armor of Agathys as アガシスの鎧, Tasha's Mind Whip as ターシャの精神の鞭, Pulse Wave as 脈波, Pulse Wave: Push/Pull as 脈波（押し出し）/脈波（引き寄せ）, Caustic Zap as 腐食電撃, Malediction as 悪呪, Demiplane as 擬似次元界, Greater Invisibility as 上級不可視化, Flame Strike as 天罰の火, Flaming Sphere as 炎の球体, Floating Disk as 浮遊盤, Fox's Cunning as 狐の知力, Owl's Wisdom as 梟の判断力, Cat's Grace as 猫の敏捷力, Eagle's Splendor as 鷲の魅力, Bear's Endurance as 熊の耐久力, Bull's Strength as 雄牛の筋力, Venom Spike as 毒針, Vampiric Touch as 吸血の手, Symbol as 印形, Circle of Power as 力の円環, and Parry as 受け流し in system text.
- Translate Aspect of the Moon as 観月, Eldritch Mind as 妖術の心, Spirit Rally as 精魂招集, Dragon Duo as 双竜, Pact of the Blade as 剣の契約, Pact Weapon as 契約武器, Improved Pact Weapon as 契約武器強化, Spiritual Pact Weapon as 霊的契約武器, Electrifying Touch as 帯電打撃, Armor Master as 防具の達人, Burning Touch as 火炎の手, Icy Touch as 氷の手, Melting Touch as 溶解の手, Toxic Touch as 毒の手, Mighty Blow as 豪打, Master Alchemist as 錬金術の達人, Forest Runner as 森渡り, Ready or Not as 待ち構え, Raise Shield as 盾を構える, Rush to Battle as 戦場への突進, Twin Blade as 双刃, Lock Breaker as 錠破り, Daunting Push as 威圧の押し出し, Lightning Launcher as 稲妻投射器, Lightning Bombs as 電撃爆弾, and Lightning Spear as 電撃の槍 in system text.
- Translate Bardic Inspiration as バードの声援, Bardic Inspiration die as 声援ダイス, Rage as 激怒, and Cantrip as 初級呪文 in system text. When a reaction button literally means generic \"Cast Spell\", translate it as 呪文 rather than 初級呪文.
- Translate Lay on Hands as 癒しの手, Repelling Blast as 拒絶の怪光線, Dodge as 回避, Uncanny Dodge as 直感回避, Shadowy Dodge as 朧影, Arcane Deflection as 跳ね返しの秘術, Attunement as 同調, Strike of Chaos as 混沌の一撃, Dazzle as 幻惑, Reaction Shot as 反応射撃, Recall Item as アイテム回収, Deflection as 偏向, Unsettled as 動揺, Deflect Missiles as 矢止め, Return Missile as 矢返し, Break Free as 脱出, Dash as 早足, Beacon of Hope as 希望のともしび, Acolyte as 侍祭, Archmage as 大魔道士, Invisible Stalker as インヴィジブル・ストーカー, Redscar Orc as レッドスカー族のオーク, and Badlands Spider as バッドランズ・スパイダー in system text.
- Translate repo-specific spell names as Mind Twist => 精神ねじり, Shine => 発光, Sparkle => きらめき, Thunderstorm => 雷雨, Gravity Slam => 重力撃, Divine Blade => 神剣, Arcane Sword => 秘術の剣, Shadow Dagger => 影の短剣, Shadow Armor => 影の鎧, and Annoying Bee => 幻蜂 in system text.
- Translate Power Word Stun as 力の言葉:朦朧 in system text.
- Translate Chain Lightning as 連鎖電撃 in system text.
- Translate Elemental Bane as 元素禍 in system text.
- Translate Countercharm as 心を守る歌 in system text.
- Translate Flight as 飛行 in system text.
- Translate Flame Arrows as 火矢 in system text.
- Translate Lightning Arrow as 電撃の矢 in system text.
- Translate Mind Blank as 空白の心 in system text.
- Translate Shapechange as 変幻自在 in system text.
- Translate Weird as 不吉な運命 in system text.
- Translate Chill Touch as 負力の接触 in system text.
- Translate Mass Heal as 集団大治癒 in system text.
- Translate Teleport as 瞬間移動, Teleporter as 瞬間移動装置, and Teleportation Circle as 瞬間移動の魔法円 in system text.
- Translate Banishing Smite as 放逐の一撃, Reverse Gravity as 重力反転, Dragon's Breath as 竜の吐息, and Cloud of Daggers as 短剣の群れ in system text.
- Translate Savage Attack as 凶暴な一撃, Savage Attacker as 凶暴な戦士, and Savage Attacks as 猛打 in system text.
- Translate Spell Scroll as 呪文の巻物 and Scroll as 巻物 in item contexts, but keep UI scroll operations as スクロール.
- Translate Protection from Energy as 元素からの保護, Stoneskin as 石の皮膚, Warding Bond as 守りの紐帯, the spell Resistance as 抵抗力, and generic damage-halving resistance as 抵抗 in system text.
- Translate Finesse weapon-property labels as 妙技.
- Translate Heavy weapon-property labels as 重武器, but do not rewrite weapon nouns such as Heavy Crossbow.
- In system explanations, spell, feature, item, invocation, and feat references inside prose should use 〈...〉, while title keys stay bracket-free. Reserve full-width round brackets such as （直線） and （輪） for variants only.
- Translate Piercing damage/type labels as 刺突, but do not rewrite ordinary verbs such as 突き刺す.
- Translate Illusion as 幻術 when it is a magic school/system tag and as 幻 in general rules text.
- Translate Charmed as 魅了状態 for condition/status titles and 魅了 for effect prose.
- Translate Opportunity Attack / Attack of Opportunity as 機会攻撃 in system text.
- Translate Major Gate as メジャーゲート in repo-specific player-facing text.
- Translate Eldritch Versatility as 妖術術式, Versatility as 術式, Versatility Switch as 術式切替, Eldritch Pool as 妖術プール, Eldritch Point as 妖術点, and Feat Eldritch Versatility Adept as 妖術術式の達人 in repo-specific system text.
- Translate reaction button Pass as 見送る, but use スキップ for generic screen pass/skip labels.
- Translate Illuminated as 照らされた, Illuminating Strike as 照らしの一撃, and Illuminating Burst as 照らしの爆発 in repo-specific system text.
- Use ヒューマン for race labels and settings such as Race/&HumanTitle and EnableAlternateHuman, but keep 人間 for generic prose.
- Use 氏族 for clan labels, 姓 for family-name labels, and 異名 for nickname labels in UI text.
- Translate Thunder Step as 雷鳴の一跳び in system text.
- Translate Blinding Smite as 目潰す一撃 in system text.
- Translate Foresight as 予知 in system text.
- Translate Power Word Heal as 力の言葉：癒し in system text.
- Translate Power Word Kill as 力の言葉：死 in system text.
- Translate Psychic Scream as 心砕く叫び in system text.
- Translate Far Step as 遠くへの一跳び in system text.
- Translate Aura of Vitality as 活力のオーラ in system text.
- Translate Branding Smite as 烙印の一撃 in system text.
- Translate Thunderous Smite as 雷鳴の一撃 in system text.
- Translate Time Stop as 時間停止 in system text.
- Translate Divine Smite as 神聖なる一撃 and Improved Divine Smite as 神聖なる攻撃 in system text.
- Translate Radiant damage/type labels as 光輝 in system text.
- Translate immunity / immune as 完全耐性 in player-facing text.
- Translate Daylight as 陽光 in system text.
- Translate Weapon Mastery as 武器マスタリー in system text.
- Translate Eldritch Invocation as 妖術.
- Translate initiative as イニシアチブ.
- Translate short rest as 小休憩 and long rest as 大休憩.
- Translate saving throw as セーヴィング・スロー.
- In prose, do not leave ability abbreviations such as CON/WIS/INT/STR/DEX/CHA; expand them into Japanese ability names such as 耐久力 and 判断力.
- Translate Sentinel as 守護戦士 when it is the feat name, and use 見張り only for non-feat/general contexts.
- Skill names must use 〈〉, for example 〈魔法学〉 and 〈ペテン〉.
- In system explanations, spell references should use 〈…〉 and feat references should use official feat names without 《》.
- Feat and feature titles that vary by ability score must use the base title plus a full-width suffix such as ［魅力］, ［知力］, ［判断力］, ［筋力］, ［敏捷力］, or ［耐久力］.
- Never leave feat/UI labels as 偉業; use 特技.
- Use official feat names exactly, for example Defensive Duelist => 守りの決闘術 and Great Weapon Master => 大業物の使い手.
- For item names, keep proper names as proper names and translate the item type/common noun into natural Japanese, such as ライトブリンガーの戦斧.
- For mechanics text, prioritize exact conditions, targets, damage types, durations, scaling, action economy, and prerequisites over style.
- Preserve numbers, placeholders, tags, and source ordering whenever the line describes a rule.
- Normalize color tags as <color=#RRGGBB>…</color>; never emit shorthand tags like <#F5B486>.
- Do not split a Japanese lexical unit with color tags; wrap the whole word or remove the decoration.
- For player-facing grid/range wording in this repo, prefer マス over セル or フィート. Examples: 1マス, 2マス, 6マス.
- Avoid half-width spaces inside Japanese compounds such as ヒット・ダイス, 魔力点, アクション・ステータス, パーティーエディター, フリーアクション.
- For flavorful text, keep natural Japanese and avoid unnecessary symbolic markup; for system text, keep references explicit and consistent.
  - Use these terminology preferences:
  - Sorcery Points -> 魔力点.
  - Channel Divinity -> 神性伝導.
  - Metamagic -> 呪文修正.
  - metamagic option -> 呪文修正能力.
  - Fireball -> 火球.
  - Delayed Blast Fireball -> 遅発火球.
  - Mind Blank -> 空白の心.
  - Thunder Step -> 雷鳴の一跳び.
  - Blinding Smite -> 目潰す一撃.
  - Wild Magic -> 荒ぶる魔法.
  - Wild Magic Surge -> 魔法暴走.
  - Tides of Chaos -> 混沌潮流.
  - Bend Luck -> 運命改変.
  - Controlled Chaos -> 混沌制御.
  - Spell Bombardment -> 呪文猛撃.
  - Snow Alliance -> 雪同盟.
- Reject these older/community spellings in system text: 火の玉, 先見の明, 形状変化, 変身, 奇妙な, 奇怪, マスヒール, タイムストップ, パワーワードヒール, パワーワードキル, パワーワードスタン, サイキックスクリーム, ファーステップ, 生命のオーラ, ソーサリー・ポイント, チャネルディヴィニティ, メタマジック, イニシアティブ, 短い休息, 長い休息, セービングスロー, 呪文セーヴ 難易度, 免疫, フレイムアロー, ライトニングアロー, チェイン光ニング, チェインライトニング, カウンタースペル, ディスペルマジック, エレメンタルベイン, カウンターチャーム, フライト, スクロール, フィネス, ヘビー, チャーム, イリュージョン, オポチュニティ アタック, ブランディング・スマイト, サンダース・スマイト, ディバイン・スマイト, 改良型ディヴァイン・スマイト, ラディアント, ラジアント, レディアント, サベージアタック, サベージアタッカー, テレポート, バニシング・スマイト, リバースグラビティ, ドラゴンブレス, ダガーの雲, 枯れて咲く, アガナザールのスコーチャー, ホールド・パーソン, ホールド・モンスター, ディスインテグレイト, ディスペル・イービル・アンド・グッド, ドミネート・モンスター, ドミネート・パーソン, ディバイン・ワード, アースクエイク, ダークネス, コンテイジョン, コーン・オブ・コールド, アビ・ダルジムの恐ろしい萎縮, インビジビリティ・サイト, 灼熱のスマイト, コール・ライトニング, クラウド・キル, コマンド, ブラインドネス, カーム・エモーションズ, ブラー, 異界の汎用性, イルミネーション付き.
{chr(10).join(glossary_lines)}
"""

    return system_guidance, user_guidance


@dataclass
class TranslationBatch:
    """Represents a batch of translations for a specific file and target language."""
    source_file: str
    target_file: str
    source_lang: str
    target_lang: str
    batch_num: int
    total_batches: int
    entries: List[Tuple[str, str]]
    file_key: str  # Unique key for the file (source_file + target_lang)
    
    
@dataclass 
class FileTranslationData:
    """Holds accumulated translation data for a file."""
    target_file: str
    data: Dict[str, str] = field(default_factory=dict)
    lock: Lock = field(default_factory=Lock)
    completed_batches: int = 0
    total_batches: int = 0
    

class TranslationManager:
    """Manages concurrent translation tasks with progress tracking."""
    
    def __init__(self, max_workers: int):
        self.max_workers = max_workers
        self.file_data: Dict[str, FileTranslationData] = {}
        self.file_data_lock = Lock()
        
    def get_file_data(self, file_key: str, target_file: str, existing_data: Dict[str, str]) -> FileTranslationData:
        """Get or create FileTranslationData for a file."""
        with self.file_data_lock:
            if file_key not in self.file_data:
                self.file_data[file_key] = FileTranslationData(
                    target_file=target_file,
                    data=existing_data.copy()
                )
            return self.file_data[file_key]
    
    def save_file_if_complete(self, file_key: str) -> bool:
        """Save file if all batches are completed. Returns True if saved."""
        with self.file_data_lock:
            if file_key not in self.file_data:
                return False
            
            file_data = self.file_data[file_key]
            if file_data.completed_batches >= file_data.total_batches:
                write_localization_file(file_data.target_file, file_data.data)
                return True
        return False


def unpack_record(record: str) -> Tuple[str, str]:
    """
    Parse a single localization record into key and value.
    
    Args:
        record: A line from the localization file in format "key=value"
        
    Returns:
        Tuple of (key, value)
    """
    term = ""
    text = ""
    try:
        term, text = record.split("=", 1)
        text = text.strip()
    except ValueError:
        term = record
    
    return term, text if text != "" else "EMPTY"


def read_localization_file(filename: str) -> Dict[str, str]:
    """
    Read a localization file and return a dictionary of key-value pairs.
    
    Args:
        filename: Path to the localization file
        
    Returns:
        Dictionary mapping keys to their localized values
    """
    result = {}
    
    if not os.path.exists(filename):
        return result
    
    try:
        with open(filename, "rt", encoding="utf-8") as f:
            line_count = 0
            for line in f:
                # Remove BOM from first line if present
                if line_count == 0 and line.startswith(codecs.BOM_UTF8.decode("utf-8")):
                    line = line[1:]
                line_count += 1
                
                line = line.strip()
                if line:
                    term, text = unpack_record(line)
                    result[term] = text
    except Exception as e:
        console.print(f"[red]ERROR reading {filename}: {e}[/red]")
    
    return result


def write_localization_file(filename: str, data: Dict[str, str]):
    """
    Write localization data to a file, sorted by key.
    
    Args:
        filename: Path to the output file
        data: Dictionary of localization key-value pairs
    """
    # Create directory if it doesn't exist
    os.makedirs(os.path.dirname(filename), exist_ok=True)
    
    # Sort and write
    with open(filename, "wt", encoding="utf-8") as f:
        for key in sorted(data.keys()):
            f.write(f"{key}={data[key]}\n")


def translate_batch_api(
    entries: List[Tuple[str, str]],
    source_lang: str,
    target_lang: str,
    target_lang_code: str
) -> Dict[str, str]:
    """
    Translate a batch of entries using the LLM API.
    
    Args:
        entries: List of (key, text) tuples to translate
        source_lang: Source language name
        target_lang: Target language name
        
    Returns:
        Dictionary mapping keys to translated values
    """
    if not entries:
        return {}
    
    # Prepare the prompt
    entries_text = "\n".join([f"{i+1}. {key}={text}" for i, (key, text) in enumerate(entries)])
    
    system_guidance, user_guidance = build_target_language_guidance(target_lang_code, entries)

    prompt = f"""You are a professional translator for a video game localization project.
Translate the following game text entries from {source_lang} to {target_lang}.

IMPORTANT INSTRUCTIONS:
1. Preserve all special formatting codes like \\n (newline), {{0}}, {{1}}, etc.
2. Maintain the same key names (everything before the = sign)
3. Keep game-specific terms consistent
4. Return ONLY the translated entries in the exact same format: "key=translated_text"
5. Return each entry on a new line, numbered as shown below
6. Do not add explanations or comments
7. Keep terminology consistent across the batch
{user_guidance}

Entries to translate:
{entries_text}

Respond with the translated entries in the same numbered format."""

    # Make API call with retries
    for attempt in range(MAX_RETRIES):
        try:
            response = client.chat.completions.create(
                model=MODEL_ID,
                messages=[
                    {
                        "role": "system",
                        "content": (
                            "You are a professional game localization translator. "
                            "You provide accurate translations while preserving all formatting codes and game terminology. "
                            f"{system_guidance}"
                        ).strip()
                    },
                    {"role": "user", "content": prompt}
                ],
                temperature=0.3
            )
            
            # Parse the response
            translated_text = response.choices[0].message.content
            if not translated_text:
                raise ValueError("Empty response from API")
                
            result = {}
            
            for line in translated_text.strip().split('\n'):
                line = line.strip()
                # Remove numbering if present (e.g., "1. " or "1)")
                if line and (line[0].isdigit() or line.startswith('-')):
                    # Find the first occurrence of key=value pattern
                    equal_pos = line.find('=')
                    if equal_pos > 0:
                        # Extract everything before the number/bullet
                        key_start = 0
                        for i, char in enumerate(line):
                            if char.isalpha() or char == '/':
                                key_start = i
                                break
                        
                        key_value_part = line[key_start:]
                        try:
                            key, value = key_value_part.split('=', 1)
                            result[key.strip()] = value.strip()
                        except ValueError:
                            continue
            
            return result
            
        except Exception as e:
            if attempt < MAX_RETRIES - 1:
                time.sleep(RETRY_DELAY)
            else:
                console.print(f"[red]ERROR: Failed to translate batch after {MAX_RETRIES} attempts: {e}[/red]")
                return {}
    
    return {}


def process_translation_batch(batch: TranslationBatch, manager: TranslationManager, progress: Progress, task_id: TaskID) -> bool:
    """
    Process a single translation batch.
    
    Args:
        batch: The translation batch to process
        manager: Translation manager
        progress: Rich progress instance
        task_id: Progress task ID for this language
        
    Returns:
        True if successful
    """
    try:
        # Get file data
        file_data = manager.get_file_data(batch.file_key, batch.target_file, {})
        
        # Translate the batch
        source_lang_name = LANGUAGE_NAMES.get(batch.source_lang, batch.source_lang)
        target_lang_name = LANGUAGE_NAMES.get(batch.target_lang, batch.target_lang)
        
        translations = translate_batch_api(batch.entries, source_lang_name, target_lang_name, batch.target_lang)
        
        # Update file data
        with file_data.lock:
            for key, value in translations.items():
                if value:
                    file_data.data[key] = value
            file_data.completed_batches += 1
        
        # Update progress
        progress.update(task_id, advance=1)
        
        # Try to save file if all batches complete
        if manager.save_file_if_complete(batch.file_key):
            progress.console.print(f"[green]✓[/green] Saved: {Path(batch.target_file).name}")
        
        return True
        
    except Exception as e:
        console.print(f"[red]ERROR processing batch: {e}[/red]")
        return False


def create_translation_batches(
    source_files: List[str],
    source_lang_code: str,
    target_lang_codes: List[str],
    translations_dir: Path,
    force: bool
) -> Dict[str, List[TranslationBatch]]:
    """
    Create translation batches organized by target language.
    
    Args:
        source_files: List of source file paths
        source_lang_code: Source language code
        target_lang_codes: List of target language codes
        translations_dir: Base translations directory
        force: Whether to force retranslation
        
    Returns:
        Dictionary mapping language code to list of batches
    """
    language_batches: Dict[str, List[TranslationBatch]] = defaultdict(list)
    
    for source_file in source_files:
        source_path = Path(source_file)
        source_data = read_localization_file(source_file)
        
        if not source_data:
            continue
        
        # Get relative path from source language folder
        source_lang_dir = translations_dir / source_lang_code
        relative_path = source_path.relative_to(source_lang_dir)
        
        for target_lang_code in target_lang_codes:
            # Build target path
            target_lang_dir = translations_dir / target_lang_code
            target_filename = relative_path.stem.replace(f"-{source_lang_code}", f"-{target_lang_code}") + relative_path.suffix
            target_file = target_lang_dir / relative_path.parent / target_filename
            
            # Read existing target file
            target_data = read_localization_file(str(target_file))
            
            # Find entries that need translation
            to_translate = []
            for key, value in source_data.items():
                if value != "EMPTY":
                    if force or key not in target_data:
                        to_translate.append((key, value))
            
            if not to_translate:
                continue
            
            # Create batches for this file
            file_key = f"{source_file}::{target_lang_code}"
            total_batches = (len(to_translate) + BATCH_SIZE - 1) // BATCH_SIZE
            
            for i in range(0, len(to_translate), BATCH_SIZE):
                batch_entries = to_translate[i:i + BATCH_SIZE]
                batch_num = i // BATCH_SIZE + 1
                
                batch = TranslationBatch(
                    source_file=source_file,
                    target_file=str(target_file),
                    source_lang=source_lang_code,
                    target_lang=target_lang_code,
                    batch_num=batch_num,
                    total_batches=total_batches,
                    entries=batch_entries,
                    file_key=file_key
                )
                
                language_batches[target_lang_code].append(batch)
    
    return language_batches


def run_concurrent_translation(
    language_batches: Dict[str, List[TranslationBatch]],
    manager: TranslationManager
):
    """
    Run concurrent translation with progress tracking.
    
    Args:
        language_batches: Dictionary mapping language code to list of batches
        manager: Translation manager
    """
    # Create a queue for all batches
    batch_queue = Queue()
    
    # Flag to signal workers to stop
    stop_flag = {'stop': False}
    
    # Set total batches for each file
    for lang_code, batches in language_batches.items():
        file_batch_counts = defaultdict(int)
        for batch in batches:
            file_batch_counts[batch.file_key] += 1
        
        for file_key, count in file_batch_counts.items():
            file_data = manager.file_data.get(file_key)
            if file_data:
                file_data.total_batches = count
    
    # Enqueue batches in round-robin fashion across languages
    max_batches = max(len(batches) for batches in language_batches.values())
    for i in range(max_batches):
        for lang_code in sorted(language_batches.keys()):
            batches = language_batches[lang_code]
            if i < len(batches):
                batch_queue.put((lang_code, batches[i]))
    
    # Create progress bars
    with Progress(
        SpinnerColumn(),
        TextColumn("[bold blue]{task.description}"),
        BarColumn(),
        TextColumn("[progress.percentage]{task.percentage:>3.0f}%"),
        TextColumn("({task.completed}/{task.total})"),
        TimeRemainingColumn(),
        console=console
    ) as progress:
        
        # Create a task for each language
        language_tasks = {}
        for lang_code, batches in language_batches.items():
            lang_name = LANGUAGE_NAMES.get(lang_code, lang_code)
            task_id = progress.add_task(f"[cyan]{lang_name:<20}[/cyan]", total=len(batches))
            language_tasks[lang_code] = task_id
        
        # Process batches concurrently
        def process_batch_from_queue():
            while not batch_queue.empty() and not stop_flag['stop']:
                try:
                    lang_code, batch = batch_queue.get_nowait()
                    if stop_flag['stop']:
                        break
                    task_id = language_tasks[lang_code]
                    process_translation_batch(batch, manager, progress, task_id)
                except:
                    break
        
        try:
            with ThreadPoolExecutor(max_workers=manager.max_workers) as executor:
                futures = [executor.submit(process_batch_from_queue) for _ in range(manager.max_workers)]
                for future in as_completed(futures):
                    try:
                        future.result()
                    except Exception as e:
                        if not stop_flag['stop']:
                            console.print(f"[red]Worker error: {e}[/red]")
        except KeyboardInterrupt:
            console.print("\n[yellow]Stopping translation workers...[/yellow]")
            stop_flag['stop'] = True
            raise


def process_path(input_path: str, source_lang_code: str, target_lang_codes: Optional[List[str]], force: bool = False):
    """
    Process a file or directory for translation.
    
    Args:
        input_path: Path to a file or directory to translate
        source_lang_code: Source language code
        target_lang_codes: List of target language codes (None for all)
        force: If True, retranslate all entries even if they already exist
    """
    path = Path(input_path)
    
    if not path.exists():
        console.print(f"[red]ERROR: Path does not exist: {input_path}[/red]")
        return
    
    # Get translations directory
    translations_dir = None
    for parent in path.parents:
        if parent.name == 'Translations':
            translations_dir = parent
            break
    
    if not translations_dir:
        console.print(f"[red]ERROR: Could not find Translations directory in path: {input_path}[/red]")
        return
    
    # Get target language codes
    if target_lang_codes is None:
        target_lang_codes = [d.name for d in translations_dir.iterdir() if d.is_dir() and d.name != source_lang_code]
    
    # Get source files
    if path.is_file():
        source_files = [str(path)]
    elif path.is_dir():
        source_files = [str(f) for f in path.rglob("*.txt")]
    else:
        console.print(f"[red]ERROR: Invalid path type[/red]")
        return
    
    if not source_files:
        console.print("[yellow]No files found to translate[/yellow]")
        return
    
    # Create translation manager
    manager = TranslationManager(max_workers=MAX_WORKERS)
    
    # Create batches
    console.print("\n[bold]Analyzing files and creating translation tasks...[/bold]")
    language_batches = create_translation_batches(
        source_files,
        source_lang_code,
        target_lang_codes,
        translations_dir,
        force
    )
    
    if not language_batches:
        console.print("[green]All translations are up to date![/green]")
        return
    
    # Initialize file data with existing translations
    for lang_code, batches in language_batches.items():
        for batch in batches:
            existing_data = read_localization_file(batch.target_file)
            manager.get_file_data(batch.file_key, batch.target_file, existing_data)
            file_data = manager.file_data[batch.file_key]
            # Count total batches for this file
            if file_data.total_batches == 0:
                file_batch_count = sum(1 for b in batches if b.file_key == batch.file_key)
                file_data.total_batches = file_batch_count
    
    # Display summary
    total_batches = sum(len(batches) for batches in language_batches.values())
    console.print(f"\n[bold]Translation Summary:[/bold]")
    console.print(f"  Source files: {len(source_files)}")
    console.print(f"  Target languages: {len(language_batches)}")
    console.print(f"  Total batches: {total_batches}")
    console.print(f"  Concurrent workers: {MAX_WORKERS}\n")
    
    # Run translation
    try:
        run_concurrent_translation(language_batches, manager)
        console.print("\n[bold green]✓ Translation completed![/bold green]\n")
    except KeyboardInterrupt:
        console.print("\n[yellow]Translation interrupted by user. Partial progress has been saved.[/yellow]\n")
        raise


def main():
    """Main entry point for the script."""
    parser = argparse.ArgumentParser(
        description='Translate game localization files using LLM API with concurrent processing',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog='''
Examples:
  # Translate all files in a language folder (run from Scripts directory)
  python translateLLM.py en
  
  # Translate a single file (run from Scripts directory)
  python translateLLM.py en/Backgrounds-en.txt
  
  # Force retranslation of all entries (including existing ones)
  python translateLLM.py en --force
  
  # Translate to specific target language(s)
  python translateLLM.py zh-CN --target en
  python translateLLM.py zh-CN --target en,de,fr
        '''
    )
    
    parser.add_argument(
        'input_path',
        help='Path to a file or directory to translate (relative to SolastaUnfinishedBusiness/Translations/)'
    )
    
    parser.add_argument(
        '--force', '-f',
        action='store_true',
        help='Force retranslation of all entries, even if they already exist in target files'
    )
    
    parser.add_argument(
        '--target', '-t',
        type=str,
        help='Target language(s) to translate to (comma-separated). If not specified, translates to all languages.'
    )
    
    args = parser.parse_args()
    
    # Get the script directory and construct the base translations path
    script_dir = Path(__file__).parent
    translations_base = script_dir.parent / 'SolastaUnfinishedBusiness' / 'Translations'
    
    # Parse input path
    input_relative = Path(args.input_path)
    
    # Detect source language from path
    source_lang_code = None
    input_path = None
    
    # Check if first part of the path is a language code
    first_part = input_relative.parts[0] if input_relative.parts else None
    if first_part in LANGUAGE_NAMES:
        source_lang_code = first_part
        input_path = translations_base / input_relative
    else:
        # Try to detect language code from anywhere in the path
        for lang_code in LANGUAGE_NAMES.keys():
            if lang_code in input_relative.parts:
                source_lang_code = lang_code
                input_path = Path(args.input_path)
                if not input_path.is_absolute():
                    input_path = translations_base / input_relative
                break
    
    if not source_lang_code or input_path is None:
        console.print("[red]ERROR: Could not detect source language from path.[/red]")
        console.print("Path must start with or contain one of the supported language codes:")
        for code, name in LANGUAGE_NAMES.items():
            console.print(f"  {code}: {name}")
        console.print("\nExamples:")
        console.print("  python translateLLM.py en")
        console.print("  python translateLLM.py zh-CN/SubClasses/OathOfDemonHunter-zh-CN.txt")
        sys.exit(1)
    
    # Parse target languages
    target_lang_codes = None
    if args.target:
        target_lang_codes = [lang.strip() for lang in args.target.split(',')]
        # Validate target languages
        invalid_langs = [lang for lang in target_lang_codes if lang not in LANGUAGE_NAMES]
        if invalid_langs:
            console.print(f"[red]ERROR: Invalid target language(s): {', '.join(invalid_langs)}[/red]")
            console.print("Supported languages:")
            for code, name in LANGUAGE_NAMES.items():
                console.print(f"  {code}: {name}")
            sys.exit(1)
        # Remove source language from targets if present
        target_lang_codes = [lang for lang in target_lang_codes if lang != source_lang_code]
    
    # Display configuration
    console.print(Panel.fit(
        f"[bold]LLM Translation Script[/bold]\n\n"
        f"Model: {MODEL_ID}\n"
        f"Batch size: {BATCH_SIZE}\n"
        f"Max workers: {MAX_WORKERS}\n"
        f"Force mode: {'[red]ON[/red] (retranslate all)' if args.force else '[green]OFF[/green] (skip existing)'}\n"
        f"Source language: {LANGUAGE_NAMES.get(source_lang_code, source_lang_code)} ({source_lang_code})\n"
        f"Target languages: {', '.join(target_lang_codes) if target_lang_codes else 'All'}",
        title="Configuration",
        border_style="blue"
    ))
    
    try:
        process_path(str(input_path), source_lang_code, target_lang_codes, args.force)
    except KeyboardInterrupt:
        console.print("\n[yellow]Operation cancelled by user[/yellow]")
        sys.exit(130)  # Standard exit code for SIGINT
    except Exception as e:
        console.print(f"\n[red]ERROR: {e}[/red]")
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
