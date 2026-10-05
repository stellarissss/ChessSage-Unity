# 六道轮回 · BGM 搜集清单

> 本目录存放游戏背景音乐（BGM）。以下 8 首曲目由 `configs/story.json` 的
> `realms[*].bgm`、`endings[*].bgm`、`prologue.bgm` 字段引用。
> 来源：用户自行搜集 MP3 文件后放入本目录即可。
> 建议格式：MP3，时长 2-4 分钟，可无缝循环。

| # | 文件名 | 用途 | 主题 / 风格描述 |
|---|---|---|---|
| 1 | `bgm_prologue.mp3` | 序章·教室 | 温暖怀旧，钢琴为主 |
| 2 | `bgm_hell.mp3` | 地狱道·悔恨之境 | 压抑沉重，低音铜管 + 心跳鼓点 |
| 3 | `bgm_hungry.mp3` | 饿鬼道·欲望之境 | 诱惑迷幻，西塔琴 + 回声人声 |
| 4 | `bgm_animal.mp3` | 畜生道·本能之境 | 原始野性，手鼓 + 低吼 |
| 5 | `bgm_human.mp3` | 人道·理性之境 | 理性冷静，钢琴 + 拨弦 |
| 6 | `bgm_asura.mp3` | 阿修罗道·愤怒之境 | 激烈燃，战鼓 + 电吉他 |
| 7 | `bgm_heaven.mp3` | 天道·平静之境 | 平静空灵，古琴 + 梵音 |
| 8 | `bgm_tiandao_boss.mp3` | 天道 Boss 战（v1.3 新增） | 审判压迫，管风琴 + 钟声 + 低频嗡鸣 |

## 引用关系

- 序章：`prologue.bgm` → `bgm_prologue.mp3`
- 六道：`realms.{hell,hungry,animal,human,asura,heaven}.bgm` → 对应 `bgm_*.mp3`
- 天道 Boss 战：`bgm_tiandao_boss.mp3`
- 结局：悟道/真我结局复用 `bgm_heaven.mp3`；堕落结局复用 `bgm_asura.mp3`；
  轮回结局复用 `bgm_prologue.mp3`；识破结局用 `bgm_tiandao_boss.mp3`

## 备注

- 音效（SFX）位于 `../sfx/`，与本清单无关。
- 文件名必须与上表完全一致（区分大小写），否则 `story.json` 引用会 404。
