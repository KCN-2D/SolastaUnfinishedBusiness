#!/usr/bin/env python3
"""Validate Japanese system localization terminology and formatting."""

from __future__ import annotations

import argparse
import csv
import re
import sys
from collections import Counter
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
TRANSLATIONS_JA = ROOT / "SolastaUnfinishedBusiness" / "Translations" / "ja"
TRANSLATIONS_EN = ROOT / "SolastaUnfinishedBusiness" / "Translations" / "en"
UNOFFICIAL_JA = ROOT / "SolastaUnfinishedBusiness" / "UnofficialTranslations" / "ja"
OFFICIAL_EN = ROOT / "Diagnostics" / "OfficialTranslations-en.txt"
GLOSSARY_PATH = ROOT / "Scripts" / "ja_glossary.tsv"

PHASE1_TRANSLATIONS = [
    "Settings-ja.txt",
    "Others-ja.txt",
    "Level20-ja.txt",
    "Feats/**/*.txt",
    "Spells/**/*.txt",
    "SubClasses/**/*.txt",
]

PHASE1_UNOFFICIAL = [
    "Feature-ja.txt",
    "Rules-ja.txt",
    "Spell-ja.txt",
    "Setting-ja.txt",
    "Equipment-ja.txt",
    "Skill-ja.txt",
    "Tooltip-ja.txt",
]

SYSTEM_TRANSLATIONS = [
    "Backgrounds-ja.txt",
    "FightingStyles-ja.txt",
    "Invocations-ja.txt",
    "Others-ja.txt",
    "Settings-ja.txt",
    "UI-ja.txt",
    "WeaponMastery-ja.txt",
    "Feats/**/*.txt",
    "Races/**/*.txt",
    "SubClasses/**/*.txt",
    "Spells/**/*.txt",
]

SYSTEM_UNOFFICIAL = [
    "Attribute-ja.txt",
    "Failure-ja.txt",
    "Feature-ja.txt",
    "Feat-ja.txt",
    "Invocation-ja.txt",
    "Reaction-ja.txt",
    "Rules-ja.txt",
    "Screen-ja.txt",
    "Skill-ja.txt",
    "Spell-ja.txt",
    "Stage-ja.txt",
    "Tag-ja.txt",
    "Tooltip-ja.txt",
    "TutorialStep-ja.txt",
]

FULL_TRANSLATIONS = ["**/*.txt"]
FULL_UNOFFICIAL = ["**/*.txt"]

PLACEHOLDER_RE = re.compile(r"\{[^{}]+\}")
URL_RE = re.compile(r"https?://\S+", re.IGNORECASE)
HTML_TAG_RE = re.compile(
    r"(?:</?(?:b|i|color|sprite)\b[^>]*>|<#[0-9A-Fa-f]{6,8}>)",
    re.IGNORECASE,
)
BAD_COLOR_SHORTHAND_RE = re.compile(r"<#[0-9A-Fa-f]{6,8}>")
OPEN_COLOR_TAG_RE = re.compile(r"<color(?:=[^>]+)?>", re.IGNORECASE)
CLOSE_COLOR_TAG_RE = re.compile(r"</color>", re.IGNORECASE)
JAPANESE_CHAR_RE = r"[一-龠々ぁ-んァ-ヶー]"
MID_WORD_JP_CHAR_RE = r"[一-龠々ァ-ヶー]"
MID_WORD_COLOR_TAG_RE = re.compile(
    rf"<color(?:=[^>]+)?>{MID_WORD_JP_CHAR_RE}</color>{MID_WORD_JP_CHAR_RE}|"
    rf"{MID_WORD_JP_CHAR_RE}<color(?:=[^>]+)?>{MID_WORD_JP_CHAR_RE}</color>"
)
ASCII_WORD_RE = re.compile(r"[A-Za-z][A-Za-z0-9+#'/-]{2,}")
HEX_COLOR_RE = re.compile(r"#[0-9A-Fa-f]{6,8}")
ASCII_SOURCE_RE = re.compile(r"^[A-Za-z0-9 +#'/-]+$")
BROKEN_VALUE_KEY_RE = re.compile(
    r"(?:^|=)(?:Action|Attribute|Background|Campaign|Caption|CharacterFamily|Condition|ContentPack|"
    r"EnvironmentEffect|Equipment|Failure|Faction|Feat|Feature|Feedback|Format|Functor|Gadget|"
    r"Invocation|Item|Language|Legal|Location|MainMenu|Map|Marketing|Message|Modal|Monster|"
    r"MonsterAttacks|Narration|NPC|Power|Prop|Quest|Race|Reaction|RestActivity|Rules|Screen|"
    r"Setting|Skill|Spell|Stage|Status|Tag|Tip|Tooltip|Tutorial|TutorialStep|UI)/&"
)
BROKEN_PLACEHOLDER_RE = re.compile(r"\b(?:TBC|TBD)\b|Error 500|WHO[？?]", re.IGNORECASE)

COMMON_ENGLISH_TOKENS = {
    "Cantrip",
    "Day",
    "Eldritch",
    "Invocation",
    "Loading",
    "Wildshape",
}

WHITELIST_TOKENS = {
    "AC",
    "ALT",
    "AMD",
    "BG3",
    "CTRL",
    "DC",
    "DEX",
    "DLC",
    "FXAA",
    "HP",
    "INT",
    "JSON",
    "Ki",
    "NPC",
    "NPCs",
    "OGL",
    "PNG",
    "Solasta",
    "SRD",
    "Steam",
    "STR",
    "UI",
    "UMM",
    "URL",
    "VSync",
    "WASD",
    "Wiki",
    "Xbox",
}

SKILL_VARIANTS = {
    "アクロバット",
    "アルカナ",
    "パフォーマンス",
    "ペテン",
    "医学",
    "威圧",
    "宗教",
    "手先の早業",
    "捜査",
    "歴史",
    "生存",
    "知覚",
    "自然",
    "芸能",
    "脅迫",
    "調査",
    "説得",
    "賺し",
    "隠密",
    "魔法学",
}

AMBIGUOUS_SPELL_TERMS = {
    "Detect Magic",
    "Dispel Magic",
    "感染",
    "英雄",
    "Guidance",
    "Identify",
    "Jump",
    "Light",
    "Longstrider",
    "Magic Stone",
    "Shield",
    "Sleep",
    "Slow",
    "Spider Climb",
    "ブレス",
    "ガイダンス",
    "緑炎の刃",
    "グリース",
    "ジャンプ",
    "シールド",
    "スパイダークライム",
    "スリープ",
    "スロー",
    "ライト",
    "レビテート",
    "レヴィテート",
    "眠り",
    "傷を負わせる",
    "負傷",
}

SOURCE_TOKEN_MISMATCH_EXCEPTIONS = {
    "Caption/&TargetMultipleCaption",
    "Caption/&TargetMultipleUniqueCaption",
    "Caption/&TargetProximityMultipleCaption",
    "Caption/&TargetProximitySingleCaption",
    "Caption/&TargetRequiredConditionCaption",
    "Caption/&TargetRequiredCreatureTypeCaption",
    "Caption/&TargetRequiredUnarmoredCaption",
    "Caption/&TargetShareUniqueCaption",
    "Feature/&PowerInnovationArmorSwitchModeGuardianDescription",
    "Stage/&AbilityScoresStageTitle",
}

SPELL_SURFACE_RE = re.compile(
    r"^(?:"
    r"Spell/&.+(?:Title|Description)|"
    r"Equipment/&Scroll.+(?:Title|Description)|"
    r"Rules/&.+(?:Title|Description)|"
    r"Screen/&.+(?:Title|Description)|"
    r"Reaction/&SubitemSelect.+Description|"
    r"Invocation/&.+(?:Title|Description)|"
    r"Condition/&.+(?:Title|Description)|"
    r"Failure/&Must.+|"
    r"Feat/&.+Description|"
    r"Feature/&.+Description|"
    r"Tooltip/&.+Description|"
    r"Tag/&.+Title|"
    r"Attribute/&.+Title|"
    r"Stage/&.+Title"
    r")$"
)

GENERAL_SYSTEM_KEY_RE = re.compile(
    r"^(?:Action|Attribute|Background|Condition|Equipment|Failure|Feat|Feature|Feedback|"
    r"FightingStyle|Invocation|Item|Language|ModUI|ModUi|Monster|Reaction|RestActivity|Rules|"
    r"Screen|Skill|Spell|Stage|Subclass|Tag|Tooltip|UI|CharacterFamily)/&"
)

INVOCATION_CONTEXT_RE = re.compile(
    r"(?:Invocation|CastInvocation|FeatEldritchAdept|ProficiencyToggleInvocation|"
    r"TrendInvocation|PointPoolWarlockInvocation|Tag.+Invocation)"
)

ABILITY_SUFFIX_RE = re.compile(
    r"[\[【]\s*(?:Cha|Int|Wis|Con|Dex|Str|チャ|ウィス|ウィズ|デックス|コン|スト|インテ|ウィスコンシン州)\s*[\]】]"
)
ENGLISH_FEAT_RE = re.compile(r"\bfeats?\b", re.IGNORECASE)

SYSTEM_KEY_EXPECTATIONS = {
    "Tooltip/&FeatTitle": "特技",
    "Tooltip/&FeatGroupTitle": "特技グループ",
    "Attribute/&TagRaceFeatTitle": "種族の特技",
    "Attribute/&HitPointsTitle": "ヒット・ポイント",
    "Attribute/&TagInvocationSpellTitle": "妖術の呪文",
    "Attribute/&StrengthTitle": "STR",
    "Attribute/&DexterityTitle": "DEX",
    "Attribute/&ConstitutionTitle": "CON",
    "Attribute/&IntelligenceTitle": "INT",
    "Attribute/&WisdomTitle": "WIS",
    "Attribute/&CharismaTitle": "CHA",
    "Skill/&ArcanaTitle": "〈魔法学〉",
    "Skill/&DeceptionTitle": "〈ペテン〉",
    "Skill/&HistoryTitle": "〈歴史〉",
    "Skill/&IntimidationTitle": "〈威圧〉",
    "Skill/&InvestigationTitle": "〈捜査〉",
    "Skill/&NatureTitle": "〈自然〉",
    "Skill/&PerformanceTitle": "〈芸能〉",
    "Skill/&PersuasionTitle": "〈説得〉",
    "Skill/&ReligionTitle": "〈宗教〉",
    "Skill/&SleightOfHandTitle": "〈手先の早業〉",
    "Feat/&FeatWarCasterTitle": "戦場の術者",
    "Feat/&FeatCleavingAttackTitle": "大業物の使い手",
    "Feat/&FeatDefensiveDuelistTitle": "守りの決闘術",
    "FightingStyle/&SentinelTitle": "守護戦士",
    "Action/&CleavingAttackToggleTitle": "大業物の使い手",
    "Condition/&ConditionFeatCleavingAttackFinishTitle": "大業物の使い手",
    "Tooltip/&CleavingAttackConcentration": "大業物の使い手を無効化します。",
    "Reaction/&UseDefensiveDuelistTitle": "守りの決闘術",
    "Reaction/&ReactionAttackSentinelTitle": "守護戦士",
    "Equipment/&WeaponTagLoadingTitle": "装填",
    "Modal/&LoadingTipTitle": "ヒント",
    "Screen/&KnownCantripsTitle": "既知の初級呪文",
    "Screen/&ProficiencyToggleInvocationTitle": "妖術",
    "Rules/&SpellLevel0FormatTitle": "初級呪文",
    "Rules/&ConditionTemporaryHitPointsTitle": "一時ヒット・ポイント",
    "Attribute/&TagClassCantripTitle": "クラスの初級呪文",
    "Spell/&AcidArrowTitle": "酸の矢",
    "Spell/&AnimalShapesTitle": "集団動物化",
    "Spell/&AnimateDeadTitle": "死体操り",
    "Spell/&BaneTitle": "破滅の予感",
    "Spell/&BanishmentTitle": "放逐",
    "Spell/&BladeBarrierTitle": "刃の障壁",
    "Spell/&FireBoltTitle": "炎の矢",
    "Spell/&MageArmorTitle": "魔道士の鎧",
    "Spell/&BlessTitle": "祝福",
    "Spell/&BlightTitle": "枯死",
    "Spell/&DarkvisionTitle": "暗視",
    "Spell/&EyebiteTitle": "魔眼",
    "Spell/&FearTitle": "恐怖",
    "Spell/&FireShieldTitle": "炎の盾",
    "Spell/&FireStormTitle": "火炎嵐",
    "Spell/&FlyTitle": "飛行",
    "Spell/&GlobeOfInvulnerabilityTitle": "耐魔法球",
    "Spell/&GlyphOfWardingTitle": "守りの秘文",
    "Spell/&GreaterRestorationTitle": "上級回復術",
    "Spell/&GuardianOfFaithTitle": "信仰の守護者",
    "Spell/&GuidanceTitle": "導き",
    "Spell/&HarmTitle": "大致傷",
    "Spell/&HealTitle": "大治癒",
    "Spell/&HeatMetalTitle": "金属加熱",
    "Spell/&KnockTitle": "解錠",
    "Spell/&LesserRestorationTitle": "初級回復術",
    "Spell/&LightningBoltTitle": "電撃",
    "Spell/&MagicCircleTitle": "防御円",
    "Spell/&MagicWeaponTitle": "魔法の武器",
    "Spell/&PrayerOfHealingTitle": "癒しの祈祷",
    "Spell/&PrismaticSprayTitle": "虹色の噴射",
    "Spell/&ProtectionFromEvilGoodTitle": "善悪からの保護",
    "Spell/&RayOfEnfeeblementTitle": "衰弱光線",
    "Spell/&RegenerateTitle": "再生",
    "Spell/&RemoveCurseTitle": "呪いの除去",
    "Spell/&ResistanceTitle": "抵抗力",
    "Spell/&ResurrectionTitle": "蘇生",
    "Spell/&RevivifyTitle": "緊急復活",
    "Spell/&SacredFlameTitle": "聖なる炎",
    "Spell/&SanctuaryTitle": "聖域",
    "Spell/&ShatterTitle": "破砕",
    "Spell/&SilenceTitle": "静寂",
    "Spell/&SpellWardTitle": "呪文防壁",
    "Spell/&TrueStrikeTitle": "百発百中",
    "Spell/&WallOfFireTitle": "火の壁",
    "Spell/&WallOfForceTitle": "力場の壁",
    "Spell/&DelayedBlastFireballTitle": "遅発火球",
    "Spell/&AganazzarScorcherTitle": "アガナザーの火炎放射",
    "Spell/&BlindnessTitle": "視覚・聴覚剥奪",
    "Spell/&BlurTitle": "かすみ",
    "Spell/&CallLightningTitle": "招雷",
    "Spell/&CalmEmotionsTitle": "感情鎮静化",
    "Spell/&CommandTitle": "命令",
    "Spell/&CloudKillTitle": "殺戮の雲",
    "Spell/&ConeOfColdTitle": "冷気噴射",
    "Spell/&ContagionTitle": "感染",
    "Spell/&DisintegrateTitle": "分解",
    "Spell/&DispelEvilAndGoodTitle": "善悪解呪",
    "Spell/&DivineWordTitle": "神言",
    "Spell/&DominateMonsterTitle": "怪物支配",
    "Spell/&DominatePersonTitle": "人物支配",
    "Spell/&EarthquakeTitle": "地震",
    "Spell/&DarknessTitle": "暗闇",
    "Spell/&CreateFoodTitle": "食糧の創造",
    "Spell/&DeathWardTitle": "死からの守り",
    "Spell/&EyebiteTitle": "魔眼",
    "Spell/&GuidingBoltTitle": "導きの矢",
    "Spell/&GustOfWindTitle": "強風",
    "Spell/&HolyAuraTitle": "聖なるオーラ",
    "Spell/&HoldMonsterTitle": "怪物金縛り",
    "Spell/&HoldPersonTitle": "対人金縛り",
    "Spell/&HypnoticPatternTitle": "催眠文様",
    "Spell/&IceStormTitle": "氷の嵐",
    "Spell/&PassWithoutTraceTitle": "跡を残さぬ移動",
    "Spell/&PhantasmalKillerTitle": "幻の殺し屋",
    "Spell/&RaiseDeadTitle": "死者の復活",
    "Spell/&SlowTitle": "減速",
    "Spell/&Spell_Flameblade_Title": "炎の刃",
    "Spell/&SpikeGrowthTitle": "トゲ密生",
    "Spell/&SpiritualWeaponTitle": "心霊武器",
    "Spell/&SunbeamTitle": "陽光",
    "Spell/&SunburstTitle": "陽光爆発",
    "Spell/&TelekinesisTitle": "念動力",
    "Spell/&ElementalWeaponTitle": "元素武器",
    "Spell/&TonguesTitle": "言語会話",
    "Spell/&TrueSeeingTitle": "真実の目",
    "Spell/&ViciousMockeryTitle": "悪意ある嘲り",
    "Spell/&MindBlankTitle": "空白の心",
    "Spell/&BoomingStepTitle": "雷鳴の一跳び",
    "Spell/&BlindingSmiteTitle": "目潰す一撃",
    "Spell/&FlameArrowsTitle": "火矢",
    "Spell/&LightningArrowTitle": "電撃の矢",
    "Spell/&ForesightTitle": "予知",
    "Spell/&MassHealTitle": "集団大治癒",
    "Spell/&PowerWordHealTitle": "力の言葉：癒し",
    "Spell/&PowerWordKillTitle": "力の言葉：死",
    "Spell/&PowerWordStunTitle": "力の言葉:朦朧",
    "Spell/&PsychicScreamTitle": "心砕く叫び",
    "Spell/&ShapechangeTitle": "変幻自在",
    "Spell/&FarStepTitle": "遠くへの一跳び",
    "Action/&FarStepTitle": "遠くへの一跳び",
    "Condition/&ConditionFarStepTitle": "遠くへの一跳び",
    "Spell/&AuraOfVitalityTitle": "活力のオーラ",
    "Spell/&ChainLightningSpellTitle": "連鎖電撃",
    "Spell/&ElementalBaneTitle": "元素禍",
    "Condition/&ConditionAuraOfVitalityTitle": "活力のオーラ",
    "Spell/&BrandingSmiteTitle": "烙印の一撃",
    "Rules/&ConditionMarkedByBrandingSmiteTitle": "烙印の一撃",
    "Spell/&ThunderousSmiteTitle": "雷鳴の一撃",
    "Spell/&TimeStopTitle": "時間停止",
    "Spell/&WeirdTitle": "不吉な運命",
    "Spell/&ChillTouchTitle": "負力の接触",
    "Spell/&BanishingSmiteTitle": "放逐の一撃",
    "Spell/&ReverseGravityTitle": "重力反転",
    "Spell/&DragonsBreathSpellTitle": "竜の吐息",
    "Spell/&CloudOfDaggersTitle": "短剣の群れ",
    "Spell/&StrikeWithTheWindTitle": "微風の打撃",
    "Condition/&ConditionFlameArrowsTitle": "火矢",
    "Condition/&ConditionLightningArrowTitle": "電撃の矢",
    "Condition/&ConditionMindBlankTitle": "空白の心",
    "Condition/&ConditionWeirdTitle": "不吉な運命",
    "Condition/&ConditionDragonsBreathSpellTitle": "竜の吐息",
    "Feat/&FeatSavageAttackTitle": "凶暴な一撃",
    "Feature/&SavageAttacksTitle": "猛打",
    "Feature/&PowerBardCountercharmTitle": "心を守る歌",
    "Condition/&ConditionStrikeWithTheWindTitle": "微風の打撃",
    "Condition/&ConditionStrikeWithTheWindAttackTitle": "微風の打撃",
    "Feature/&PowerStrikeWithTheWindTitle": "微風の打撃",
    "CharacterFamily/&HumanoidTitle": "人型生物",
    "Item/&Greatsword_Bearclaw_Title": "ベアクロウ・グレートソード",
    "Item/&Battleaxe_Lightbringer_Title": "ライトブリンガー・バトルアックス",
    "Feature/&PowerDruidWildShapeTitle": "自然の化身",
    "Feature/&PowerCircleOfTheNightWildShapeCombatTitle": "戦う自然の化身",
    "Feature/&PowerCircleOfTheNightPrimalStrikeTitle": "原始の打撃",
    "Feature/&PowerSorcerousWildMagicD02Title": "火球",
    "Feature/&PowerSorcerousWildMagicBendLuckTitle": "運命改変",
    "Feature/&PowerSorcerousWildMagicControlledChaosTitle": "混沌制御",
    "Feature/&PowerSorcerousWildMagicTidesOfChaosTitle": "混沌潮流",
    "Feature/&PowerSorcerousWildMagicWildMagicSurgeTitle": "魔法暴走",
    "Feature/&FeatureSorcerousWildMagicSpellBombardmentTitle": "呪文猛撃",
    "Attribute/&SorceryPointsTitle": "魔力点",
    "Feature/&ClericChannelDivinityTitle": "神性伝導",
    "Feature/&PointPoolSorcererMetamagicTitle": "呪文修正",
    "Feature/&PaladinDivineSmiteTitle": "神聖なる一撃",
    "Feature/&PaladinImprovedDivineSmiteTitle": "神聖なる攻撃",
    "Feature/&FeatureSetDivineSmite2024Title": "パラディンの神聖なる一撃",
    "Screen/&ProficiencyToggleMetamagicTitle": "呪文修正",
    "Rules/&RateSorceryPointsFormatTitle": "魔力点",
    "Modal/&DamageAffinityTypeImmunityTitle": "完全耐性",
    "Tooltip/&TagFinesseTitle": "妙技",
    "Tooltip/&TagHeavyTitle": "重武器",
    "Tooltip/&TagPolearmWeaponTitle": "長柄武器",
    "Tooltip/&TagScrollTitle": "巻物",
    "FightingStyle/&PolearmExpertTitle": "長柄の使い手",
    "Feat/&FeatHeavyArmorMasterTitle": "重装鎧の達人",
    "Spell/&MazeTitle": "迷路",
    "Spell/&WallOfThornsTitle": "イバラの壁",
    "Spell/&WallOfThornsLineTitle": "イバラの壁（直線）",
    "Spell/&WallOfThornsRingTitle": "イバラの壁（輪）",
    "Size/&GargantuanTitle": "超巨大",
    "Reaction/&ReactionAttackAoOEnterTitle": "機会攻撃",
    "Action/&EldritchVersatilityTitle": "バーサティリティ",
    "Feature/&PowerEldritchVersatilityPointPoolTitle": "エルドリッチ・バーサティリティ",
    "Condition/&ConditionEldritchAegisAddACTitle": "エルドリッチ・イージス",
    "Condition/&ConditionEldritchWardAddSaveTitle": "エルドリッチ・ウォード",
    "Feature/&PowerInnovationWeaponArcaneJoltTitle": "力場の衝撃",
    "Item/&MonsterAttackSteelDefenderTitle": "力場の斬撃",
    "Condition/&ConditionPathOfTheLightEyesOfTruthTitle": "不可視視認",
    "Condition/&ConditionPathOfTheLightIlluminatedTitle": "照らされた",
    "Rules/&DamagePiercingTitle": "刺突",
    "Tooltip/&TagShapeChangeTitle": "変幻自在",
    "Rules/&DamageRadiantTitle": "光輝",
    "Setting/&DamageRadiantStyleTitle": "光輝ダメージスタイル",
    "NamedPlace/&SNOWALLIANCE_Title": "雪同盟",
    "Subclass/&SorcerousWildMagicTitle": "荒ぶる魔法",
    "Screen/&JournalAdventureTitle": "<color=#F5B486>冒険</color><color=#F5B486>記録</color>",
    "Screen/&JournalBestiaryTitle": "<color=#F5B486>モンスター図鑑</color>",
    "Screen/&JournalFactionStatusTitle": "<color=#F5B486>派閥</color>",
    "Screen/&JournalInvestigationTitle": "<color=#F5B486>調査</color>",
    "Screen/&JournalNewDayColorTitle": "<color=#F5B486>日</color>",
    "Screen/&JournalNewDayTitle": "日",
    "Screen/&JournalQuestLogTitle": "<color=#F5B486>クエスト</color><color=#F5B486>記録</color>",
    "Screen/&JournalTutorialTitle": "<color=#F5B486>チュートリアル</color>",
    "Race/&ElfTitle": "エルフ",
    "Spell/&DaylightTitle": "陽光",
    "Equipment/&ScrollDaylightTitle": "陽光の巻物",
    "EffectProxy/&ProxyDaylightTitle": "陽光",
    "Gadget/&ParameterDurationTypeDayTitle": "日",
    "Modal/&ContentPropertyDurationDayTitle": "日",
}

SYSTEM_REQUIRED_SUBSTRINGS = {
    "Feat/&FeatWarCasterDescription": ["耐久力", "セーヴィング・スロー", "初級呪文"],
    "Feat/&FeatEldritchAdeptDescription": ["妖術"],
    "Monster/&AncientRemorhaz_Regeneration_Description": ["古代レモルハズ", "ヒット・ポイント"],
    "Feature/&PowerSorcerousWildMagicBendLuckDescription": ["魔力点", "能力値判定", "セーヴィング・スロー"],
    "Feature/&PointPoolSorcererMetamagicDescription": ["呪文修正能力"],
}

MANUAL_BANNED_PATTERNS = [
    (lambda key: True, re.compile(r"キャントリップ"), "初級呪文"),
    (lambda key: True, re.compile(r"セービング(?: |)スロー"), "セーヴィング・スロー"),
    (lambda key: True, re.compile(r"セービング ロール"), "セーヴィング・スロー"),
    (lambda key: True, re.compile(r"火の玉|ファイアボール"), "火球"),
    (lambda key: True, re.compile(r"先見の明"), "予知"),
    (lambda key: key in {"Spell/&ShapechangeTitle", "Tooltip/&TagShapeChangeTitle"}, re.compile(r"形状変化|(?<!変幻自在)変身"), "変幻自在"),
    (lambda key: key in {"Spell/&WeirdTitle", "Condition/&ConditionWeirdTitle"}, re.compile(r"奇妙な|奇怪"), "不吉な運命"),
    (lambda key: "ChillTouch" in key, re.compile(r"死霊の手|チル(?:・|)タッチ"), "負力の接触"),
    (lambda key: key == "Spell/&MassHealTitle", re.compile(r"マスヒール"), "集団大治癒"),
    (lambda key: "TimeStop" in key, re.compile(r"タイムストップ"), "時間停止"),
    (lambda key: "SavageAttack" in key, re.compile(r"サベージアタック"), "凶暴な一撃"),
    (lambda key: "SavageAttacker" in key, re.compile(r"サベージアタッカー"), "凶暴な戦士"),
    (lambda key: "SavageAttacks" in key, re.compile(r"野蛮な攻撃|サベージアタック"), "猛打"),
    (lambda key: "TeleportationCircle" in key, re.compile(r"テレポーテーションサークル|テレポート"), "瞬間移動の魔法円"),
    (lambda key: "Teleporter" in key, re.compile(r"テレポーター"), "瞬間移動装置"),
    (lambda key: "Teleport" in key and "TeleportationCircle" not in key and "Teleporter" not in key, re.compile(r"テレポート"), "瞬間移動"),
    (lambda key: "BanishingSmite" in key, re.compile(r"バニシング・スマイト|追放のスマイト"), "放逐の一撃"),
    (lambda key: "ReverseGravity" in key, re.compile(r"リバースグラビティ"), "重力反転"),
    (lambda key: "DragonsBreath" in key, re.compile(r"ドラゴンブレス|ドラゴンの息"), "竜の吐息"),
    (lambda key: "CloudOfDaggers" in key, re.compile(r"ダガーの雲"), "短剣の群れ"),
    (lambda key: "WitherAndBloom" in key, re.compile(r"枯れて咲く"), "ウィザー・アンド・ブルーム"),
    (lambda key: True, re.compile(r"パワーワードヒール"), "力の言葉：癒し"),
    (lambda key: True, re.compile(r"パワーワードキル"), "力の言葉：死"),
    (lambda key: True, re.compile(r"サイキックスクリーム"), "心砕く叫び"),
    (lambda key: True, re.compile(r"ファーステップ"), "遠くへの一跳び"),
    (lambda key: "AuraOfVitality" in key, re.compile(r"生命のオーラ"), "活力のオーラ"),
    (lambda key: "AganazzarScorcher" in key, re.compile(r"アガナザールのスコーチャー"), "アガナザーの火炎放射"),
    (lambda key: "HoldPerson" in key, re.compile(r"ホールド・パーソン"), "対人金縛り"),
    (lambda key: "HoldMonster" in key, re.compile(r"ホールド・モンスター"), "怪物金縛り"),
    (lambda key: "Disintegrate" in key, re.compile(r"ディスインテグレイト"), "分解"),
    (lambda key: "DispelEvilAndGood" in key, re.compile(r"ディスペル・イービル・アンド・グッド"), "善悪解呪"),
    (lambda key: "DominateMonster" in key, re.compile(r"ドミネート・モンスター|ドミネイトモンスター"), "怪物支配"),
    (lambda key: "DominatePerson" in key, re.compile(r"ドミネート・パーソン|ドミネイトパーソン"), "人物支配"),
    (lambda key: "DivineWord" in key, re.compile(r"ディバイン・ワード|ディヴァイン・ワード"), "神言"),
    (lambda key: "Earthquake" in key, re.compile(r"アースクエイク|アースクウェイク"), "地震"),
    (lambda key: "Darkness" in key, re.compile(r"ダークネス"), "暗闇"),
    (lambda key: "Contagion" in key, re.compile(r"コンテイジョン"), "感染"),
    (lambda key: "ConeOfCold" in key, re.compile(r"コーン・オブ・コールド"), "冷気噴射"),
    (lambda key: "AbiDalzim" in key, re.compile(r"アビ・ダルジムの恐ろしい萎縮"), "アビー・ダルジムの恐るべき枯渇"),
    (lambda key: "SeeInvisibility" in key, re.compile(r"インビジビリティ・サイト"), "不可視視認"),
    (lambda key: "SearingSmite" in key, re.compile(r"灼熱のスマイト"), "灼熱の一撃"),
    (lambda key: "CallLightning" in key, re.compile(r"コール・ライトニング"), "招雷"),
    (lambda key: "CloudKill" in key, re.compile(r"クラウド・キル|クラウドキル"), "殺戮の雲"),
    (lambda key: "Command" in key, re.compile(r"コマンド"), "命令"),
    (lambda key: "Blindness" in key, re.compile(r"ブラインドネス"), "視覚・聴覚剥奪"),
    (lambda key: "CalmEmotions" in key, re.compile(r"カーム・エモーションズ"), "感情鎮静化"),
    (lambda key: "Blur" in key, re.compile(r"ブラー"), "かすみ"),
    (lambda key: "CreateFood" in key, re.compile(r"クリエイト・フード"), "食糧の創造"),
    (lambda key: "DeathWard" in key, re.compile(r"デス・ウォード"), "死からの守り"),
    (lambda key: "Eyebite" in key, re.compile(r"アイバイト|Eyebite"), "魔眼"),
    (lambda key: "GuidingBolt" in key, re.compile(r"ガイディング・ボルト|導きのボルト"), "導きの矢"),
    (lambda key: "GustOfWind" in key, re.compile(r"ガスト・オヴ・ウィンド"), "強風"),
    (lambda key: "HolyAura" in key, re.compile(r"ホーリー・オーラ|ホーリー オーラ|ホーリィ・オーラ"), "聖なるオーラ"),
    (lambda key: "HypnoticPattern" in key, re.compile(r"ヒプノティック・パターン|催眠パターン"), "催眠文様"),
    (lambda key: "IceStorm" in key, re.compile(r"アイス・ストーム|アイスストーム|氷嵐"), "氷の嵐"),
    (lambda key: "PassWithoutTrace" in key, re.compile(r"パス・ウィズアウト・トレイス"), "跡を残さぬ移動"),
    (lambda key: "PhantasmalKiller" in key, re.compile(r"ファンタズマル・キラー|ファンタズマルキラー"), "幻の殺し屋"),
    (lambda key: "RaiseDead" in key, re.compile(r"レイズ・デッド|死者蘇生"), "死者の復活"),
    (lambda key: "Flameblade" in key or "FlameBlade" in key, re.compile(r"フレイムブレード"), "炎の刃"),
    (lambda key: "SpikeGrowth" in key, re.compile(r"スパイク・グロウス"), "トゲ密生"),
    (lambda key: "SpiritualWeapon" in key, re.compile(r"スピリチュアル・ウェポン|(?<!心)霊武器"), "心霊武器"),
    (lambda key: "Sunbeam" in key, re.compile(r"サンビーム"), "陽光"),
    (lambda key: "Sunburst" in key, re.compile(r"サンバースト"), "陽光爆発"),
    (lambda key: "Tongues" in key, re.compile(r"タンズ|異言"), "言語会話"),
    (lambda key: "TrueSeeing" in key, re.compile(r"トゥルー・シーイング|真視|真の洞察"), "真実の目"),
    (lambda key: "ViciousMockery" in key, re.compile(r"ヴィシャス・モッカリ[ィー]"), "悪意ある嘲り"),
    (lambda key: "AcidArrow" in key, re.compile(r"アシッド(?:・|)アロー"), "酸の矢"),
    (lambda key: "AnimalShapes" in key, re.compile(r"アニマル(?:・|)シェイプ"), "集団動物化"),
    (lambda key: "AnimateDead" in key, re.compile(r"アニメート(?:・|)デッド"), "死体操り"),
    (lambda key: "Banishment" in key, re.compile(r"追放"), "放逐"),
    (lambda key: "BladeBarrier" in key, re.compile(r"ブレード(?: |・|)バリア"), "刃の障壁"),
    (lambda key: "Blight" in key, re.compile(r"ブライト"), "枯死"),
    (lambda key: "Darkvision" in key, re.compile(r"ダーク(?:・|)ビジョン"), "暗視"),
    (lambda key: "Eyebite" in key, re.compile(r"アイバイト"), "魔眼"),
    (lambda key: "Fear" in key, re.compile(r"フィアー"), "恐怖"),
    (lambda key: "FireShield" in key, re.compile(r"ファイア(?:ー)?(?:・|)シールド|コールド・シールド|ウォームシールド"), "炎の盾"),
    (lambda key: "FireStorm" in key, re.compile(r"ファイア(?:ー)?(?:・|)ストーム"), "火炎嵐"),
    (lambda key: key == "Spell/&FlyTitle", re.compile(r"フライ"), "飛行"),
    (lambda key: "GlobeOfInvulnerability" in key, re.compile(r"グローブ・オヴ・インヴァルナラビリティ"), "耐魔法球"),
    (lambda key: "GlyphOfWarding" in key, re.compile(r"グリフ・オブ・ウォーディング"), "守りの秘文"),
    (lambda key: "GreaterRestoration" in key, re.compile(r"グレーター・レストレーション"), "上級回復術"),
    (lambda key: "GuardianOfFaith" in key, re.compile(r"ガーデイァン・オブ・フェイス"), "信仰の守護者"),
    (lambda key: key == "Spell/&GuidanceTitle", re.compile(r"ガイダンス"), "導き"),
    (lambda key: "Harm" in key, re.compile(r"ハーム"), "大致傷"),
    (lambda key: key == "Spell/&HealTitle", re.compile(r"ヒール"), "大治癒"),
    (lambda key: "HeatMetal" in key, re.compile(r"ヒート(?:・|)メタル"), "金属加熱"),
    (lambda key: "Knock" in key and "KnockOut" not in key and "KnockProne" not in key, re.compile(r"ノック"), "解錠"),
    (lambda key: "LesserRestoration" in key, re.compile(r"レッサー・レストレーション"), "初級回復術"),
    (lambda key: "LightningBolt" in key, re.compile(r"ライトニング(?:・|)ボルト"), "電撃"),
    (lambda key: "MagicCircle" in key, re.compile(r"マジック(?:・|)サークル"), "防御円"),
    (lambda key: "MagicWeapon" in key, re.compile(r"マジック(?:・|)ウエポン"), "魔法の武器"),
    (lambda key: "PrayerOfHealing" in key, re.compile(r"プレイヤー・オブ・ヒーリング"), "癒しの祈祷"),
    (lambda key: "PrismaticSpray" in key, re.compile(r"プリズマティック(?:・|)スプレー"), "虹色の噴射"),
    (lambda key: "ProtectionFromEvilGood" in key, re.compile(r"プロテクション・フロム・イービル・アンド・グッド"), "善悪からの保護"),
    (lambda key: "RayOfEnfeeblement" in key, re.compile(r"レイ・オブ・エンフィーブルメント"), "衰弱光線"),
    (lambda key: "Regenerate" in key, re.compile(r"リジェネレイト"), "再生"),
    (lambda key: "RemoveCurse" in key, re.compile(r"リムーブ・カース|呪い解除"), "呪いの除去"),
    (lambda key: "Resistance" in key and key != "Feature/&DamageResistanceFormat" and key != "Modal/&DamageAffinityTypeResistanceTitle", re.compile(r"レジスタンス"), "抵抗力 / 抵抗"),
    (lambda key: "Resurrection" in key, re.compile(r"リザレクション|復活"), "蘇生"),
    (lambda key: "Revivify" in key, re.compile(r"リヴィヴィファイ|蘇生"), "緊急復活"),
    (lambda key: "SacredFlame" in key, re.compile(r"サークレット・フレーム"), "聖なる炎"),
    (lambda key: "Sanctuary" in key, re.compile(r"サンクチュアリ"), "聖域"),
    (lambda key: "Shatter" in key, re.compile(r"シャター"), "破砕"),
    (lambda key: "Silence" in key, re.compile(r"サイレンス"), "静寂"),
    (lambda key: "SpellWard" in key, re.compile(r"スペル(?:・|)ウォード"), "呪文防壁"),
    (lambda key: "TrueStrike" in key, re.compile(r"トゥルー(?:・|)ストライク"), "百発百中"),
    (lambda key: "WallOfFire" in key, re.compile(r"ウォール・オブ・ファイア"), "火の壁"),
    (lambda key: "WallOfForce" in key, re.compile(r"ウォール・オブ・力場"), "力場の壁"),
    (lambda key: "PowerWordStun" in key, re.compile(r"パワーワードスタン"), "力の言葉:朦朧"),
    (lambda key: "ChainLightning" in key or "AllowTargetingSelectionWhenCastingChainLightningSpell" in key, re.compile(r"チェイン光ニング|チェインライトニング"), "連鎖電撃"),
    (lambda key: "Counterspell" in key or "CounterSpell" in key, re.compile(r"カウンタースペル"), "呪文妨害"),
    (lambda key: "DispelMagic" in key, re.compile(r"ディスペルマジック"), "魔法解呪"),
    (lambda key: "ElementalBane" in key, re.compile(r"エレメンタルベイン"), "元素禍"),
    (lambda key: "Countercharm" in key or "CounterCharm" in key, re.compile(r"カウンターチャーム"), "心を守る歌"),
    (lambda key: "Flight" in key, re.compile(r"フライト"), "飛行"),
    (lambda key: "TagFinesse" in key or "LongswordFinesse" in key, re.compile(r"フィネス"), "妙技"),
    (
        lambda key: key in {
            "Tooltip/&TagHeavyTitle",
            "Feat/&FeatGreatWeaponDefenseDescription",
            "Feat/&FeatCleavingAttackDescription",
            "FightingStyle/&LungerDescription",
            "Equipment/&WeaponTagHeavyTitle",
        },
        re.compile(r"ヘビー|重い"),
        "重武器",
    ),
    (
        lambda key: "Scroll" in key
        and key not in {
            "Setting/&ScrollSensitivityDescription",
            "Setting/&ScrollSensitivityTitle",
            "Screen/&GamepadHintScrollConsole",
            "Screen/&GamepadHintVerticalScroll",
        },
        re.compile(r"スクロール"),
        "巻物",
    ),
    (
        lambda key: "EldritchVersatility" in key,
        re.compile(r"異界の汎用性|汎用性(?!の達人)|汎用性切替"),
        "エルドリッチ・バーサティリティ / バーサティリティ",
    ),
    (lambda key: "Illusion" in key, re.compile(r"イリュージョン"), "幻 / 幻術"),
    (lambda key: "Charm" in key or "Charmed" in key, re.compile(r"チャームド|チャーム"), "魅了 / 魅了状態"),
    (
        lambda key: "PathOfTheLight" in key,
        re.compile(r"インビジビリティ・サイト|イルミネーション付き|イルミネーションストライク|イルミネイティング"),
        "不可視視認 / 照らされた / 照らしの一撃",
    ),
    (
        lambda key: (
            "DamagePiercing" in key
            or "TagDamagePiercing" in key
            or key in {
                "Feedback/&AdditionalDamagePiercerLine",
                "Feature/&PowerFiendishResiliencePiercingTitle",
                "Feature/&PowerSorcerousWildMagicD17Title",
                "Feature/&FeatureSetPatronTreeOneWithTheTreeDescription",
                "Setting/&DamagePiercingStyleDescription",
                "Setting/&DamagePiercingStyleTitle",
            }
        ),
        re.compile(r"ピアス|貫通|突き刺し"),
        "刺突",
    ),
    (lambda key: "MajorGate" in key or "CampaignNodePortalDescription" in key or "FailureTeleportNoPortal" in key, re.compile(r"Major Gate|メジャー ゲート"), "メジャーゲート"),
    (lambda key: True, re.compile(r"オポチュニティ アタック"), "機会攻撃"),
    (lambda key: "PolearmExpert" in key or "PolearmMaster" in key, re.compile(r"ポールアームマスター|ポールアーム"), "長柄の使い手 / 長柄武器"),
    (lambda key: "HeavyArmorMaster" in key, re.compile(r"重装鎧マスター"), "重装鎧の達人"),
    (lambda key: True, re.compile(r"ディス有利"), "不利"),
    (lambda key: True, re.compile(r"テレキネシス"), "念動力"),
    (lambda key: True, re.compile(r"エレメンタル(?:・|)ウェポン"), "元素武器"),
    (lambda key: True, re.compile(r"マインドブランク"), "空白の心"),
    (lambda key: True, re.compile(r"サンダーステップ"), "雷鳴の一跳び"),
    (lambda key: True, re.compile(r"ブラインディング・スマイト"), "目潰す一撃"),
    (lambda key: True, re.compile(r"ブランディング・スマイト"), "烙印の一撃"),
    (lambda key: True, re.compile(r"サンダース・スマイト|サンダラス・スマイト"), "雷鳴の一撃"),
    (lambda key: True, re.compile(r"ディバイン・スマイト"), "神聖なる一撃"),
    (lambda key: True, re.compile(r"改良型ディヴァイン・スマイト"), "神聖なる攻撃"),
    (lambda key: True, re.compile(r"フレイムアロー"), "火矢"),
    (lambda key: True, re.compile(r"ライトニングアロー"), "電撃の矢"),
    (lambda key: True, re.compile(r"ラディアント|ラジアント|レディアント"), "光輝"),
    (lambda key: "Sentinel" in key or "守護戦士" in key or key.startswith("FightingStyle/&Sentinel"), re.compile(r"センチネル"), "守護戦士"),
    (lambda key: "InnovationWeaponArcaneJolt" in key, re.compile(r"力場ジョルト"), "力場の衝撃"),
    (lambda key: "MonsterAttackSteelDefender" in key, re.compile(r"力場スラッシュ"), "力場の斬撃"),
    (lambda key: "EldritchAegis" in key, re.compile(r"異界のイージス"), "エルドリッチ・イージス"),
    (lambda key: "EldritchWard" in key, re.compile(r"異界の護り"), "エルドリッチ・ウォード"),
    (lambda key: True, re.compile(r"混沌の潮流"), "混沌潮流"),
    (lambda key: True, re.compile(r"ベンドラック"), "運命改変"),
    (lambda key: True, re.compile(r"制御された混沌"), "混沌制御"),
    (lambda key: True, re.compile(r"ワイルドマジックサージ"), "魔法暴走"),
    (lambda key: True, re.compile(r"ワイルドマジック(?!サージ)"), "荒ぶる魔法"),
    (lambda key: True, re.compile(r"呪文爆撃"), "呪文猛撃"),
    (lambda key: True, re.compile(r"Snow Alliance|スノー ?アライアンス"), "雪同盟"),
    (lambda key: True, re.compile(r"イニシアティブ"), "イニシアチブ"),
    (lambda key: True, re.compile(r"短い休(?:息|憩|み)|小休み|小休止"), "小休憩"),
    (lambda key: True, re.compile(r"長い休(?:息|憩|み)"), "大休憩"),
    (lambda key: True, re.compile(r"チャネル(?:・|)ディヴィニティ|チャンネル神性|チャネル神性|Divinityをチャネル|ディヴィニティをチャンネル?化"), "神性伝導"),
    (lambda key: True, re.compile(r"メタマジック(?:の)? ?オプション|メタマジック"), "呪文修正 / 呪文修正能力"),
    (lambda key: True, re.compile(r"神性伝導:"), "神性伝導："),
    (lambda key: INVOCATION_CONTEXT_RE.search(key) is not None, re.compile(r"呼び出し"), "妖術"),
    (
        lambda key: GENERAL_SYSTEM_KEY_RE.match(key) is not None,
        re.compile(r"(?<![A-Za-z])HP(?![A-Za-z])"),
        "ヒット・ポイント",
    ),
    (lambda key: True, re.compile(r"ヒット ダイス|ヒットダイス"), "ヒット・ダイス"),
    (lambda key: True, re.compile(r"【(?:筋力|敏捷力|耐久力|知力|判断力|魅力)】"), "能力値名の全角括弧を削除"),
    (lambda key: True, re.compile(r"《(?:長柄の使い手|重装鎧の達人)》"), "特技名の山括弧を削除"),
    (lambda key: True, re.compile(r"メイズ"), "迷路"),
    (lambda key: "WallOfThorns" in key, re.compile(r"いばらの壁\((?:ライン|リング)\)|いばらの壁"), "イバラの壁（直線） / イバラの壁（輪） / イバラの壁"),
    (lambda key: True, re.compile(r"ガルガンチュアン"), "超巨大"),
    (lambda key: "FiendishResilience" in key, re.compile(r"悪魔の回復力|Fiendish Resilience"), "魔物の抵抗力"),
    (lambda key: True, re.compile(r"細胞"), "マス / 房 / 独房など文脈に応じて修正"),
    (lambda key: True, re.compile(r"ソーサリー・ポイント|ソーサリー ポイント|ソーサリーポイント|魔術ポイント"), "魔力点"),
    (lambda key: True, re.compile(r"免疫"), "完全耐性"),
    (lambda key: True, re.compile(r"\b(?:CON|DEX|STR|INT|WIS|CHA)セーヴィング・スロー"), "能力値名を日本語にしてください"),
    (lambda key: True, re.compile(r"\b(?:CON|DEX|STR|INT|WIS|CHA)ボーナス"), "能力値名を日本語にしてください"),
    (lambda key: True, re.compile(r"\b(?:CON|DEX|STR|INT|WIS|CHA)修正"), "能力値名を日本語にしてください"),
    (lambda key: True, re.compile(r"フリー アクション"), "フリーアクション"),
    (lambda key: True, re.compile(r"力場 ダメージ"), "力場ダメージ"),
    (lambda key: True, re.compile(r"力場 ポイント"), "力場ポイント"),
    (lambda key: True, re.compile(r"呪文 難易度|スペル セーブ 難易度|スペルセーブ難易度"), "呪文セーヴ難易度"),
    (lambda key: True, re.compile(r"呪文セーヴ 難易度"), "呪文セーヴ難易度"),
    (lambda key: True, re.compile(r"ソーサラー ポイント"), "魔力点"),
    (lambda key: True, re.compile(r"アクション ステータス"), "アクション・ステータス"),
    (lambda key: True, re.compile(r"\b(?:5|10|15|20|25|30|35|40|45|50|55|60|90|120)\s*フィート\b"), "マス換算距離"),
    (lambda key: True, re.compile(r"\b\d+\s*セル\b"), "マス換算距離"),
    (lambda key: True, re.compile(r"\b\d+\s+マス\b"), "スペースなしのマス表記"),
    (lambda key: True, re.compile(r"空きセル|空のセル|同じセル|隣接するセル|セル内|セルに|セルを|セルへ|セル分|セル タイプ"), "マス"),
]


def is_feat_system_key(key: str) -> bool:
    return (
        key.startswith("Feat/&")
        or key.startswith("Tooltip/&Feat")
        or key.startswith("Attribute/&Tag") and "Feat" in key
        or key.startswith("ModUi/&DocsFeats")
        or "ModFeats" in key
        or "EnableFeats" in key
        or "Feat" in key and key.startswith(("Screen/&", "Stage/&", "UI/&"))
    )


def is_title_like_key(key: str) -> bool:
    return key.endswith(("Title", "Header")) or key.startswith("UI/&CustomFeatureSelectionTooltipType")


def has_unbalanced_color_tags(value: str) -> bool:
    return len(OPEN_COLOR_TAG_RE.findall(value)) != len(CLOSE_COLOR_TAG_RE.findall(value))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Check Japanese localization consistency.")
    parser.add_argument("--phase1", action="store_true", help="Scan the original phase-1 target files only.")
    parser.add_argument("--system", action="store_true", help="Scan the broader system/UI profile.")
    parser.add_argument("--full-ja", action="store_true", help="Scan the full Japanese tree.")
    args = parser.parse_args()
    enabled = [flag for flag in (args.phase1, args.system, args.full_ja) if flag]
    if len(enabled) > 1:
        parser.error("--phase1, --system, and --full-ja are mutually exclusive")
    return args


def load_tsv_glossary(path: Path) -> dict[str, dict[str, str]]:
    glossary: dict[str, dict[str, str]] = {}
    with path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle, delimiter="\t")
        for row in reader:
            glossary[row["source"]] = row
    return glossary


def load_key_value_file(path: Path) -> dict[str, str]:
    entries: dict[str, str] = {}
    if not path.exists():
        return entries
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        if not raw_line or "=" not in raw_line:
            continue
        key, value = raw_line.split("=", 1)
        entries[key] = value
    return entries


def collect_files(base: Path, patterns: list[str]) -> list[Path]:
    files: set[Path] = set()
    for pattern in patterns:
        files.update(base.glob(pattern))
    return sorted(files)


def source_entries_for(path: Path, official_en: dict[str, str]) -> dict[str, str]:
    if path.is_relative_to(TRANSLATIONS_JA):
        relative = path.relative_to(TRANSLATIONS_JA)
        source_name = relative.name.replace("-ja", "-en")
        source_path = TRANSLATIONS_EN / relative.parent / source_name
        return load_key_value_file(source_path)
    return official_en


def normalize_control_token(token: str) -> str:
    shorthand = re.fullmatch(r"<#([0-9A-Fa-f]{6,8})>", token)
    if shorthand:
        return f"<color=#{shorthand.group(1).upper()}>"
    color_open = re.fullmatch(r"<color=#([0-9A-Fa-f]{6,8})>", token, re.IGNORECASE)
    if color_open:
        return f"<color=#{color_open.group(1).upper()}>"
    if token.lower() == "</color>":
        return "</color>"
    return token


def extract_control_tokens(text: str) -> Counter[str]:
    tokens = Counter(PLACEHOLDER_RE.findall(text))
    tokens.update(re.findall(r"\\n", text))
    tokens.update(normalize_control_token(tag) for tag in HTML_TAG_RE.findall(text))
    return tokens


def english_tokens(value: str) -> list[str]:
    scrubbed = URL_RE.sub(" ", value)
    scrubbed = HEX_COLOR_RE.sub(" ", scrubbed)
    tokens: list[str] = []
    for token in ASCII_WORD_RE.findall(scrubbed):
        if token in WHITELIST_TOKENS:
            continue
        if token.startswith("sprite") or token.startswith("Index"):
            continue
        if token in COMMON_ENGLISH_TOKENS:
            tokens.append(token)
    return tokens


def skill_variant_patterns(alt: str) -> list[re.Pattern[str]]:
    escaped = re.escape(alt)
    return [
        re.compile(rf"(?:(?<=\()|(?<=（)){escaped}(?:(?=\))|(?=）))"),
        re.compile(rf"(?<!〈){escaped}(?!〉)(?:判定|チェック|スキル)"),
    ]


def literal_pattern(term: str) -> re.Pattern[str]:
    if term == "ニック":
        return re.compile(r"(?<![ァ-ヶー])ニック(?!(?:[ァ-ヶー]|ネーム))")
    if term == "放射":
        return re.compile(r"(?<!火炎)放射")
    if ASCII_SOURCE_RE.fullmatch(term):
        return re.compile(rf"\b{re.escape(term)}\b")
    return re.compile(re.escape(term))


def build_glossary_variants(glossary: dict[str, dict[str, str]]) -> list[tuple[str, str, str, list[re.Pattern[str]]]]:
    variants: list[tuple[str, str, str, list[re.Pattern[str]]]] = []
    for row in glossary.values():
        preferred = row["preferred_ja"].strip()
        category = row["category"].strip()
        alt_text = row["alt_ja"].strip()
        source = row["source"].strip()

        for alt in [part.strip() for part in alt_text.split("|") if part.strip()]:
            if source == "Eldritch Invocation" and alt == "呼び出し":
                continue
            if category == "spell" and alt in AMBIGUOUS_SPELL_TERMS:
                continue
            if category == "skill" and alt in SKILL_VARIANTS:
                patterns = skill_variant_patterns(alt)
            else:
                patterns = [literal_pattern(alt)]
            variants.append((alt, preferred, category, patterns))

        if (
            source
            and ASCII_SOURCE_RE.fullmatch(source)
            and category in {
                "spell",
                "feat",
                "feature",
                "system",
                "weapon",
                "armor",
                "item",
                "creature",
                "creature_type",
                "class_feature",
                "ui",
                "proper_noun",
                "skill",
            }
            and not (category == "spell" and source in AMBIGUOUS_SPELL_TERMS)
        ):
            variants.append((source, preferred, category, [literal_pattern(source)]))

    return variants


def should_check_variant(key: str, category: str) -> bool:
    if category == "spell":
        return SPELL_SURFACE_RE.match(key) is not None
    if category == "skill":
        return key.startswith("Skill/&") or "Guidance" in key
    if category == "ability":
        return key.startswith(
            (
                "Action/&",
                "Attribute/&",
                "Condition/&",
                "Feat/&",
                "Feature/&",
                "Reaction/&",
                "Rules/&",
                "Screen/&",
                "Skill/&",
                "Spell/&",
                "Stage/&",
                "Tag/&",
                "Tooltip/&",
                "UI/&",
            )
        )
    if category == "feat":
        return (
            key.startswith("Feat/&")
            or ("Feat" in key and key.endswith(("Title", "Description")))
            or SPELL_SURFACE_RE.match(key) is not None
        )
    if category == "feature":
        return SPELL_SURFACE_RE.match(key) is not None or INVOCATION_CONTEXT_RE.search(key) is not None
    if category in {"weapon", "armor", "item"}:
        return key.startswith(
            (
                "Action/&",
                "Condition/&",
                "Equipment/&",
                "Feat/&",
                "Feature/&",
                "Item/&",
                "Reaction/&",
                "Screen/&",
                "Tooltip/&",
            )
        )
    if category == "proper_noun":
        return True
    if category == "creature":
        return key.startswith(
            (
                "CharacterFamily/&",
                "Equipment/&",
                "Feat/&",
                "Feature/&",
                "Language/&",
                "Monster/&",
                "Tooltip/&",
            )
        )
    if category in {"ui", "class_feature"}:
        return GENERAL_SYSTEM_KEY_RE.match(key) is not None and not key.startswith(
            (
                "Campaign/&",
                "Narration/&",
                "Quest/&",
            )
        )
    if category in {"system", "damage_type", "creature_type", "subclass"}:
        return GENERAL_SYSTEM_KEY_RE.match(key) is not None and not key.startswith(
            (
                "Narration/&",
                "Quest/&",
            )
        )
    return GENERAL_SYSTEM_KEY_RE.match(key) is not None


def main() -> int:
    args = parse_args()
    if args.phase1:
        profile = "phase1"
        translation_patterns = PHASE1_TRANSLATIONS
        unofficial_patterns = PHASE1_UNOFFICIAL
    elif args.full_ja:
        profile = "full"
        translation_patterns = FULL_TRANSLATIONS
        unofficial_patterns = FULL_UNOFFICIAL
    else:
        profile = "system"
        translation_patterns = SYSTEM_TRANSLATIONS
        unofficial_patterns = SYSTEM_UNOFFICIAL

    glossary = load_tsv_glossary(GLOSSARY_PATH)
    variants = build_glossary_variants(glossary)
    official_en = load_key_value_file(OFFICIAL_EN)

    files = collect_files(TRANSLATIONS_JA, translation_patterns)
    files += collect_files(UNOFFICIAL_JA, unofficial_patterns)

    errors: list[str] = []

    for path in files:
        source_entries = source_entries_for(path, official_en)
        seen_keys: set[str] = set()
        for line_no, raw_line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            if not raw_line.strip():
                errors.append(f"{path}:{line_no}:format blank line")
                continue

            if "=" not in raw_line:
                errors.append(f"{path}:{line_no}:format missing '='")
                continue

            key, value = raw_line.split("=", 1)
            source_value = source_entries.get(key)
            if key in seen_keys:
                errors.append(f"{path}:{line_no}:format duplicate key '{key}'")
            seen_keys.add(key)
            if not value.strip() and (source_value is None or source_value.strip()):
                errors.append(f"{path}:{line_no}:format empty value")
            if profile in {"system", "full"} and BROKEN_VALUE_KEY_RE.search(value):
                errors.append(f"{path}:{line_no}:format embedded key in value")
            if profile in {"system", "full"} and BROKEN_PLACEHOLDER_RE.search(value):
                errors.append(f"{path}:{line_no}:format unresolved placeholder text")
            if profile in {"system", "full"} and BAD_COLOR_SHORTHAND_RE.search(value):
                errors.append(f"{path}:{line_no}:format shorthand color tag")
            if profile in {"system", "full"} and has_unbalanced_color_tags(value):
                errors.append(f"{path}:{line_no}:format unbalanced color tags")
            if profile in {"system", "full"} and MID_WORD_COLOR_TAG_RE.search(value):
                errors.append(f"{path}:{line_no}:format color tag splits Japanese word")

            expected = SYSTEM_KEY_EXPECTATIONS.get(key)
            if profile in {"system", "full"} and expected is not None and value != expected:
                errors.append(f"{path}:{line_no}:expected '{expected}'")

            required_terms = SYSTEM_REQUIRED_SUBSTRINGS.get(key, [])
            if profile in {"system", "full"}:
                for required in required_terms:
                    if required not in value:
                        errors.append(f"{path}:{line_no}:missing '{required}'")

            for predicate, pattern, preferred in MANUAL_BANNED_PATTERNS:
                if profile in {"system", "full"} and predicate(key) and pattern.search(value):
                    errors.append(f"{path}:{line_no}:term '{pattern.pattern}' -> '{preferred}'")

            if profile == "full":
                if is_feat_system_key(key) and "偉業" in value:
                    errors.append(f"{path}:{line_no}:term '偉業' -> '特技'")
                if (key.startswith(("Feat/&", "Feature/&")) and key.endswith("Title")) and ABILITY_SUFFIX_RE.search(value):
                    errors.append(f"{path}:{line_no}:term ability suffix must use ［能力値名］")
                if is_feat_system_key(key) and ENGLISH_FEAT_RE.search(value):
                    errors.append(f"{path}:{line_no}:term 'feat' -> '特技'")

            for alt, preferred, category, patterns in variants:
                if not should_check_variant(key, category):
                    continue
                if alt and alt in preferred and preferred in value:
                    continue
                if any(pattern.search(value) for pattern in patterns):
                    errors.append(f"{path}:{line_no}:term '{alt}' -> '{preferred}'")

            if source_value is not None and key not in SOURCE_TOKEN_MISMATCH_EXCEPTIONS:
                if extract_control_tokens(source_value) != extract_control_tokens(value):
                    errors.append(f"{path}:{line_no}:format control tokens differ from source")

            for token in english_tokens(value):
                errors.append(f"{path}:{line_no}:english '{token}'")

    errors = sorted(set(errors))

    if errors:
        for error in errors:
            print(error)
        print(f"\n{len(errors)} issue(s) found.")
        return 1

    print("No issues found.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
