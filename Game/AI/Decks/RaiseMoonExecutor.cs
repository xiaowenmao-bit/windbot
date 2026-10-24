using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    // 升月（盈彩月夜）—— 无检索抽卡循环卡组
    // 设计依据：cards.cdb 效果原文 + B站教学视频 BV1uwYv6YEKX 的转写
    //
    // 卡组本质：
    //   全系列没有任何检索 → 只能靠抽卡把 Lv7 怪捞出来；
    //   6 张下级都带「被抽到时 → 给对方看 → 自跳」→ 抽卡就是展开；
    //   刃/灯/朔 还带「从手牌丢弃自身 → 抽 1」→ 手牌里的 Lv7 是抽卡引擎，不是废牌；
    //   终端分两条线：
    //     A. 长线（EX 区）：一掷乾坤（魔法师族，只有它在 EX 区才有全抗 + 把对手检索换成对手抽 1）
    //     B. 斩杀（主要怪兽区）：千金大奖(1400) → 黑智天至(800) → 光子咆哮(4500/战阶 5300) → 反物质龙(4900) → 天霆号(4200)
    //        ★ 战阶值 = 面板 + 黑智天至 ① 的 阶级×100（FIELD 效果，持续整回合，黑智天至被叠走也还在）
    //        用户 v3.4 口径（两条分支、同一落点）：
    //          · 对方怪 ≥2 → 战前就在光子咆哮上叠反物质龙，② 拔 1 打两次怪兽；
    //          · 对方怪 ≤1 → 光子咆哮先进战阶打（反物质龙 ① 把打脸砍半，不能让它先上），
    //                        M2 再补一层反物质龙（只为给天霆号多攒 1 个素材）；
    //          · 两条最后都在反物质龙上叠天霆号 → 下个对方回合拔 2 清场。
    //        ★ 所以主要怪兽区不要放大姐 —— 大姐是 EX 区的长线封锁，主怪兽区要留给能往上叠的斩杀基底。
    //        ★ 千金大奖 ② 与斩杀线互斥：发过效果就当不了黑智天至的基底，对方前场有怪时一律不弹。
    //   ★ 只有「一掷乾坤」是魔法师族，千金大奖是机械族 —— 未眠②/群星② 的「魔法师族本家超量」分支只认大姐。
    //   ★ AI 看不到卡组内容（Bot.Deck 里未抽到的卡 Id = 0），所以任何「卡组里有没有某张卡」的判断都不能用，
    //     必须直接发动、由引擎把真实卡牌列表送进 OnSelectCard。
    //
    // v3.1（同步 2026-10-24 二次修订）：
    //   主卡组：调和之天救龙离场（它的代价会封死本家超量效果一整回合）→ 换成 小丑与锁鸟×1；
    //           灰流丽×2 / 愚蠢的副葬×2 / 幽鬼兔×3 保持不变。
    //   额外卡组：整体改回原样 —— 漆梏之食兽 / 混沌大西洲 / 谐调光师 / 巨眼 / 反物质龙 全部回来，
    //           5 张同调离场。斩杀线恢复成 R7 → R8黑智天至 → R8光子咆哮 → R9反物质龙 → R12天霆号 的完整阶梯。
    //   → 锁鸟是本卡组最舒服的手坑：全卡组零检索，封锁只砸对手，被问到就发。
    //
    // v3.2（2026-10-24 第三次修订，按用户给出各额外怪的定位重写主要怪兽区）：
    //   1) 锁鸟改成「后攻 T1 对方检索时丢」—— 先攻局留着，因为封锁会把自家西艾罗③ / 辉煌② 的抽卡一起停掉。
    //   2) 主要怪兽区不再是「无脑千金大奖」，五只针对性 R7 各有对口场景，条件不成立才让给千金大奖：
    //        混沌No.6 大西洲巨人 ← 对方 ≤3200 攻的抗性怪（3300 碾过去；当回合不能造成伤害）
    //        No.11 巨眼          ← 对方站着高价值怪兽（高攻 / 有干扰）→ 直接夺取控制权
    //        托勒密星团Ω7        ← R7×3：解场拔 N 弹 N 只怪兽回卡组 / M2 站场让对方回合弹 3
    //        No.76 谐调光师      ← 对方主打墓地（挖走墓地怪兽当自己的素材）
    //        漆梏之食兽          ← 对方主打魔法展开（闪刀姬那类），② 直接把魔法吸成素材
    //   3) 超量素材统一走 Level7Bodies()，排除场上的超量怪兽本体（超量怪不能当超量素材）。
    //
    // v3.3（2026-10-24 第四次修订，实战 replay 反馈）：
    //   1) 西艾罗② 的 cost「返 1 张卡回卡组」优先返手坑，不再返本家怪兽（本家怪是丢抽/自跳的引擎）。
    //   2) 主要怪兽区打点阶梯改成常态化：千金大奖 1400 → 黑智天至 1600 → 光子咆哮 4500（战阶 5300）。
    //      以前光子咆哮只在「刚好够斩杀」时才叠，结果黑智天至 800 经常站着什么也不做。
    //   3) 光子咆哮打完战阶 → 主要阶段 2 叠天霆号，下个对方回合拔 2 清场。
    //      素材是累加的（叠放继承基底的 overlay 组），所以 4500 那只身上通常有 4 个素材。
    //   4) 幽鬼兔不再砸「不会被效果破坏」的怪（读对方那张卡的 lua 脚本判断，白丢手牌的事不做了）。
    //
    // v3.5（2026-10-24 第六次修订，用户纠正黑智天至的加攻模型）：
    //   1) ★ 黑智天至 ① 是「这个回合战斗阶段，**自己场上所有超量怪兽**攻击力 + 各自阶级×100」，
    //      效果注册在玩家侧（FIELD + SetTargetRange(LOCATION_MZONE,0)）、持续到结束阶段，
    //      **不挂在黑智天至自己身上** —— 它被叠走加成照样在。所以光子咆哮战阶是 5300（4500+800），
    //      天霆号 4200（3000+1200）、反物质龙 4900（4000+900）。v3.4 把 5300 改回 4500 是错的。
    //   2) 千金大奖 ② 该弹 2 就弹 2：不再锁 SelectNumber(1) 保素材。
    //      与之配套的是 ③ —— 反物质龙素材不够（<3）就不勉强出，直接光子咆哮打 / M2 叠天霆号。
    //   3) 斩杀线仍是两条分支 + 同一落点：
    //        ≥2 只怪 → 主阶 1 叠反物质龙，战阶 ② 拔 1 打两次怪兽；
    //        ≤1 只怪 → 光子咆哮先进战阶打，M2 再叠反物质龙补素材（素材不够就跳过）。
    //      两条都在反物质龙 / 光子咆哮上叠天霆号。
    // v3.6（2026-10-24 第七次修订，天霆号自我连锁）：
    //   ★ 天霆号 ① 是 QUICK_O + EVENT_FREE_CHAIN，脚本里没有 SetCountLimit → 素材够就能在同一连锁上连开。
    //     AI 实测 01:27:05 / 01:27:06 连清两次，4 素材直接拔到 0，对方回合完全没有压制。
    //   新逻辑：自己回合只在「需要清对方场上卡」时开一次；只有对方要无效它（链上最后一环是对方）才补一手；
    //   其余任何情况都不许自己连锁自己；对方回合则在对方发动·贴永续 / 登场后连锁清场。
    //   目标是保住 2~4 个素材进对方回合。
    //
    // v5.7（2026-10-24 第九轮修订，replay 优化9.yrp + 13:24 截图）：
    //   1) ★ 主要怪兽区「腾格子」闸门：自己回合空闲主要怪兽区 ≤2 格时，先把场上的 7 星本家
    //      叠成超量（EX 区有空位就给大姐，否则千金大奖），把格子腾出来再继续抽卡。
    //      这条闸门排在执行器列表**第 0 位**，所以每次 idle cmd 都先于所有抽卡效果被问到 ——
    //      原因见下面「主要怪兽区空间闸门」那一段的注释（抽卡链路中间没有 idle cmd）。
    //   2) 巨眼 / 混沌大西洲 / 托勒密 三条解场线条件成立时让位给它们（EnemyXyzPlayPending）：
    //      它们一样吃 2~3 只 7 星、只占 1 格，属于总纲第 1 条的「和对方换资源」。
    //
    // v5.8（2026-10-24 第十轮修订，replay 优化9.yrp 13:20:10）：
    //   ★ 天霆号在对方回合的清场改成一张「换账表」`ZeusExchangeWorth(doomed)`：
    //     对方铺开（≥3 张）且算上 2 个素材仍然赚才清；天霆号自己将被拆时不亏就清。
    //     原来连锁分支只看「对方这一环是不是怪兽/永续」，于是对方丢一张手链（优化9 的幽鬼兔）
    //     就能把我方整场 + 2 素材换成他场上的零星几张 —— 同一个局面自由时点判「不清」，连锁分支却清了。
    //     现在 C1 连锁 / C3 自由时点 / 二次连锁（被无效）三处共用同一张表。
    //
    // v5.9（2026-10-24 第十一轮修订，replay 优化10）三处：
    //   1) ★ 返手卡优先级（HandCardValue）：**陷阱辉煌从 15 提到 80（最后才洗回）** ——
    //      用户口径「陷阱优先级很高，要留着对方回合拉大姐形成干扰」；而手牌里开不了的场地
    //      （场上已经有场地）排到 3，菈碧之后第一个洗回。
    //   2) ★ 抽卡时机统一到 `EnemyDeployed()`（帐③ / 大姐拔1抽1 / 墓地辉煌② 同一个闸门）：
    //      对方空场时抽到的希耶娜 / 库露雪换不到东西 = 血亏，删掉帐③「主阶2 / 手牌为 0」的两处硬抽兜底。
    //   3) ★ 翼③ 加速的节奏与目标：先用 `AI.SelectingEffectYn` 把诱发 ②（抽 1）和 QUICK_O ③ 分开，
    //      实现「先抽、再加速」；加速目标交给 `PreferredAccelXyz` —— 我方没大姐 + 对方在检索 →
    //      大姐（对方检索结算前被换成随机抽 1）；其余 → 千金大奖（拔 2 弹 2）。
    //
    // v5.10（2026-10-24 第十二轮修订，用户换了 1 张额外）：
    //   ★ 额外卡组用「征霸龙-烈龙（47132793，炎龙 R7×2 2800/1800）」换掉了「漆梏之食兽」。
    //     ① 拔 1 → 自己的手卡·场上 1 张 + 对方场上 1 张**破坏**（IGNITION，只能自己主阶）。
    //     自己那张选 EX 区的大姐（EFFECT_IMMUNE_EFFECT）→ 破坏不掉 → **白嫖一张破坏**。
    //     用户定位：t2 / t3 拆对方「后场魔法陷阱 / 场地」用的解场件（`XyzBlazeDragon` / `BlazeActivate`）。
    //     漆梏之食兽的执行器保留备用（卡不在卡组里，`ExtraDeck.Any` 那关永远不过）。
    //
    // v5.11（2026-10-24 第十三轮修订，用户又改构筑）：
    //   1) ★ 斩杀线换芯：**反物质龙 → 银河眼重铠光子龙（39030163）**。
    //      重铠能在「银河眼」超量（光子咆哮）上重叠，② 拔 1 素材破坏对方场上 1 张**表侧**卡。
    //      于是「拆对方场地/魔陷」不再需要额外里塞征霸龙 —— 走到 光子咆哮 之后叠重铠顺手拆掉就行：
    //        千金大奖 → 黑智天至 → 光子咆哮(5300 战阶) → **重铠(4800 战阶 + 拆 1 张)** → 天霆号
    //      M1 叠的条件：有值得拆的表侧卡且本回合收不掉人头（否则留着 5300 先打）；
    //      M2 叠的条件：有值得拆的表侧卡 —— 拆完再叠天霆号，素材数一样还白赚一张破坏。
    //   2) ★ 征霸龙换出卡组，**漆梏之食兽装回来**：只在「对方墓地通常·速攻魔法 ≥3 张」时出
    //      （闪刀 / 光与暗的仪式那类），后场盖卡 ≥2 张时让位给千金大奖「拔 2 弹 2」，
    //      并删掉「千金大奖用尽 → 第 4 只 R7」的兜底。
    //
    // v5.12（2026-10-24 第十四轮修订，replay 优化12）三处：
    //   1) 增殖的G（`UseMaxxC`）：只在**连锁里**发（C2+，链上最后一环是对方的卡），
    //      不再抢「对方回合开始 / 自由时点」—— 等对方把要特招的动作摆上桌再跟。
    //   2) 雅·卡萝尔（`OnSelectPosition`）：从手牌特招一律**守备表示**（1400/1400 攻守同值），
    //      只有「本回合能收人头」或「场上正好有她打得死且值得杀的目标」才攻出去；
    //      落地守备不影响后续 `ReposTradeUp` 把她翻过来打。
    //   3) 返手卡优先级（`HandCardValue`）按用户口径对齐成一条：
    //      菈碧 ＞ 手坑 ＞ **未眠之城（15）** ＞ 本家怪兽（60） ＞ 群星（70） ＞ 辉煌（80）。
    //
    // v5.13（2026-10-24 第十五轮修订，replay _LastReplay 16:02:10）：
    //   ★ 陨石（尼比鲁）漏在 `HandCardValue` 的所有档位之外 → 落到兜底 30，比未眠之城（15）还晚洗回：
    //     那一手手牌同时有「陨石 + 未眠之城」，结果把场地洗回卡组、把陨石留在手里。
    //     修：陨石 = 9（手坑里最后才洗回），并把它加进 `HandTrapIds`（No.76 数对方墓地要排除；
    //     翼③ 也不能把「对方从手牌发动陨石」当成「他要特招怪兽」的证据）。
    //
    // v5.14（2026-10-24 第十六轮修订，replay _LastReplay 16:11~16:15 整场）：
    //   ★ 用户口径：**升月的抽卡效果要另开连锁** —— 自己回合也一样，不许 C2 跟着自己的卡上链。
    //     例外只有两张「这次不抽就永远抽不到」的：
    //       · 帐·西艾罗 ③：这张自己将被解掉（被取对象 / 群体清场）→ 连锁抢开；
    //       · 辉煌 ②（墓地）：墓地这张将被除外（被对方取对象）/ 被本家洗回卡组 → 连锁抢开。
    //     病根：v5.13 的 `SieroActivate` 与 `SplendorActivate` 都把 `Duel.Player == 0` 分支
    //     排在 InChain 检查之前，自己回合里「大姐拔1抽1 / 帐① 自跳 / 刃灯丢手抽1」之后
    //     引擎再问抽卡，它们就直接 C2 跟上了（录像 16:14:07 辉煌→大姐、16:14:52 辉煌→帐①
    //     两处实证；帐③ 的代码结构完全一样）。同一时点两张抽卡挤进一条连锁：
    //     连锁里抽到的本家接不上「被抽到时自跳」，还白白吃掉 C1 结算后的自由时点。
    //     改法：两条都把 `InChain()` 提到玩家分支之前；例外判据（`SelfDoomedOnField` /
    //     新增 `GraveSplendorDoomed`）放在 `InChain()` 之前。大姐 v3.9 / 朔 v4.2 早已是这么写的。
    //
    // v5.15（2026-10-24 第十七轮修订，replay 优化16 + 补做优化5 / 6 / 8 的存量条目）：
    //   用户把「看录像记下来的笔记」一次给了六条（桌面 优化.txt），逐条落地：
    //   1) 群星置顶（`PickStackFrom`）：**手牌或场上**没有西艾萝 → 第一张置顶西艾萝（v5.17 补上「场上」）
    //      （场上立一只帐，对方回合还有持续抽卡，吃阻抗也不亏）；手里**有**西艾萝 → 第一张置顶卡萝尔。
    //      录像 20:55:20 实证：手里没帐，群星却置顶了菈碧（一次性件），断掉了对方回合的抽卡源。
    //   2) 超量素材（`PickMaterials`）：场上有富余的 7 星时**不用**西艾萝 / 菈碧当素材
    //      （帐要站场抽卡、翼要站场加速）；余量不够才退化到「只保菈碧」，再不够正常拿。
    //   3) 双终端在场就停手（`TerminalDuoReady`）：大姐 + 千金大奖都在场时，
    //      `XyzBounty` / `CompactToMainZone` / `CompactToExtraZone` 一律不再叠，
    //      把 7 星留到对方回合用翼 ③ 加速（本回合能收人头时例外，见 CanFinish）。
    //   4) 加速目标分档（`PreferredAccelXyz`）：对方 3 只怪 + 我方 3 只 7 星 → 托勒密Ω7 弹 3；
    //      对方在吃墓地（已露墓地利用 + 墓地非手坑怪 ≥3）→ No.76 挖墓；其余 → 千金大奖（第二张继续弹 2）。
    //   5) 通召让位（执行器注册顺序）：雅的通召下移到副葬 / 群星 / 场地**之后** ——
    //      「手里有雅又有别的抽卡部件时，先发别的抽卡效果再通召雅」。
    //   6) 表示形式（`OnSelectPosition`）：抽卡特招的本家一律守备，只有希耶娜 / 库露雪（2800）例外攻表；
    //      帐 2100/2100 补上这条（以前落到 base，随机攻/守）。
    //      并补齐千金大奖 ② 的弹卡目标（`BountyPreyValue` / `_bountyBouncePending`）：
    //      旧行为是 `cards.Take(n)` 瞎弹，现在按「高价值怪兽 / 盖放后场」排序。
    //
    // v5.16（2026-10-24 第十八轮修订，replay「2026-10-24 21-27-04.yrp」）：
    //   ★ 用户口径：「优化6 改得有点过度了 —— 这个录像第一回合敌方空场，升月回合是第二回合，
    //     应该抢血才对。所以升月本家的怪兽都应该是攻击表示特殊召唤。另外抢伤害时候就要走斩杀路线了：
    //     拿两只怪兽叠 R7，再叠黑智天至，再叠超银河眼光子龙（4500 攻）；等银河眼战斗完，
    //     主要阶段 2 再叠重铠和天霆号。」
    //   v5.15 的「本家一律守备」把「对手空场、这一回合该打脸」也一起守掉了 ——
    //   录像 21:27:15/21:27:38/21:27:47 连续三只（雅 1400 / 帐 2100 / 翼 700）全部守备特招，
    //   21:27:56 还用这票 7 星在 EX 区叠了大姐（2100），整回合唯一的打点就是库露雪 2800。
    //   改法：
    //   1) 新增 `RaceMode()`（抢血模式）：自己回合、非 T0、对方前场 ≤1 只且没有 5300 以上的墙
    //      → 这一回合就该把伤害打出去。
    //   2) `OnSelectPosition`：RaceMode 下本家下级 + 两只 R7 一律**攻击表示**（不再看 CanFinish /
    //      FindTradeTarget —— 打脸本身就是目标，不必先有「打得死的怪」）。
    //   3) 斩杀线优先：RaceMode 下 `CompactToExtraZone` 让位（2 只 7 星不拿去 EX 区叠大姐），
    //      `CompactToMainZone` 不再给大姐让路，`XyzBounty` 不因 `TerminalDuoReady` 停手；
    //      `BountyActivate` 在 RaceMode 下**不弹卡**（弹过就打不上黑智天至的 ovfilter flag，整条链断）。
    //   4) 阶梯时机对齐用户口径：光子咆哮**战斗阶段打完之后**的 M2 才叠重铠 → 天霆号
    //      （`PhotonArmorSummon` 在 M1 一律不叠，原来 M1 只要能拆就叠，等于开打前先把自己的
    //      5300 降到 4800）；`ZeusSummon` 的 M2 分支要先等重铠 ② 拆完再叠（Activate 段排在
    //      SpSummon 段之后，不让位的话重铠 ② 永远没机会发，素材白扔）。
    //
    // v5.17（2026-10-24 第十九轮修订，replay「2026-10-24 21-38-04.yrp」）：
    //   ★ 用户口径：「如果手牌**或者场上**没有西艾萝，那么第一个群星应该置顶西艾萝
    //     （西艾萝抽卡效果一回合只能发动一次），其他情况则是看情况置顶：
    //     ① 我方需要抽卡 → 卡萝尔；② 我方需要破坏对方场上的怪兽 → 希耶娜；
    //     ③ 我方要破坏对方场上的魔法陷阱卡 → 库露雪；
    //     ④ 我方场上已经有 2 张 7 星本家或者大姐，这时最好置顶菈碧以达成在对方回合加速超量干扰的效果。」
    //   v5.15 / v5.16 的病根：只查手牌，没查场上。
    //     录像 21:38 那一手：21:38:10 与 21:38:15 两只帐已经用 ② 返手卡自跳站到场上，
    //     手里当然没有帐 → 21:38:21 群星把**第三只帐**置了顶。
    //     帐 ③ 是 1 回合 1 次的，第三只上来只是个 2100 白板。
    //   改法：`PickStackFrom` 重写成「手牌+场上都没有帐 → 帐；否则 ④菈碧（场面已成）
    //     → ②希耶娜（对方有 2000+ 怪）→ ③库露雪（对方有后场魔陷）→ ①卡萝尔（默认抽卡）」，
    //     每一档都打一条 `群星 置顶理由：…` 日志，方便复盘为什么挑了这张。
    //
    // v5.18 / v5.19（2026-10-24 第二十轮修订，replay「_LastReplay.yrp」22:24:56~22:25:26）：
    //   ★ 用户口径分两次说清，最后定稿（v5.19）：
    //     「抽1效果要另开连锁，而另一个对应检索的持续抽卡效果要跟连锁。」
    //     v5.18 先把两个效果当成一件（一律另开连锁），是理解错了，一天内当场纠正。
    //     定稿：朔·斯奎兹 的两个效果**策略相反**，各走各的 ——
    //     · 「自己抽 1」：只在自由时点当 C1 发。★ v5.20 理由纠正（用户口径）：
    //       **另开连锁是为了让后续抽到的本家怪兽能跳下来** —— 「被抽到时可以特殊召唤」是抽卡那一刻的
    //       诱发，抽卡若发生在连锁中（C2+），那个诱发时点就过去了，抽到的雅 / 帐 / 菈碧 / 希耶娜 /
    //       库露雪 全烂在手里。（顺带的好处才是「不把自己塞进连锁、不堵手坑时点」，但那不是原因。）
    //     · 「持续抽卡」（对方每次把卡加入手卡我方抽 1）：必须 C2 跟对方的检索 ——
    //       它得在检索结算前挂上，否则对方这一次检索已经结算完，这一抽永远补不回来。
    //
    // v5.20（2026-10-24 第二十一轮修订，用户口径纠偏 + 手坑协同）三处：
    //   1) ★ 「抽1 为什么另开连锁」的理由纠正：不是为了挡手坑，而是为了让**连锁里抽到的本家怪兽
    //      能跳下来**（自跳是「被抽到时」的诱发，连锁中抽卡时那个时点已经过去）。
    //      行为不变（仍然另开 C1），只改判据注释与日志措辞，避免以后按错误的理由去改它。
    //   2) ★ 朔 与 灰流丽 的先后（用户口径）：对方回合手卡同时有这两张时，
    //      **第一个检索先交给灰流丽无效**；对方再开第二个检索（灰流丽 1 回合 1 次已用掉）
    //      才轮到朔挂「持续抽卡」。为此——
    //      · 执行器注册顺序重排：灰流丽 → 幽鬼兔 → 遮蒙者 → 陨石 → 锁鸟 → 增殖的G → 朔。
    //        旧顺序是朔在**第 1 段**、G 在灰流丽之前 —— 朔/G 一抢链，链上最后一环变成我方的卡，
    //        后面的无效系全部发不出（`GameAI.OnSelectChain` 外层遍历的就是注册顺序）。
    //      · 新增 `_ashUsedTurn` / `AshWillTakeChain()` / `AshYieldToJackpot()`：
    //        灰流丽实际交出去时记回合号；朔用**同一套判据**判断「这一环灰流丽会不会真的交」——
    //        大姐在场那种主动不交的情况要让朔照常跟链，否则两头都空着。
    //   3) ★ 陨石（`NibiruActivate`）不再「数到第 5 只就无条件发」，改成先做**场面资源比较**：
    //      对方表侧怪兽资源 > 我方会被解放的资源才发；对方空、我方一片怪时留着
    //      （解放全场 = 拿自己的场面白送）。里侧盖怪不参与解放、EX 区大姐免疫，都不算我方损失。
    //      估值见 `NibiruSideValue`：打点 + 800 起，额外怪 +1200，每张超量素材 +600。
    //
    // v5.21（2026-10-24 第二十二轮修订，用户口径：控制对方资源优先）三处：
    //   1) ★ 灰流丽不再受「只在对方回合」的限制。旧代码 `UseNegate` 第一行就是
    //      `if (Duel.Player != 1) return false` —— 我方回合对方从墓地/场上发检索时灰流丽整个不响应，
    //      这条链路只能落到大姐的「替换」。现在新增 `anyTurn` 开关，灰流丽传 true；
    //      真正的守门改成 `EnemyActivated()`（链上最后一环是对方的卡），比看回合精确。
    //      幽鬼兔 / 遮蒙者仍维持只在对方回合交。
    //   2) ★ 大姐在场时灰流丽的让位阈值 2 → 3：对方手牌 **≤3 就交灰流丽**，>3 才让大姐替换。
    //      理由是用户口径「控制对方资源更重要」—— 大姐 e5 只是把精准检索换成「对方随机抽 1」，
    //      对方手牌紧的时候这一张就是他的续命资源；灰流丽是连抽都不给。
    //   3) ★ 补一个漏洞：`JackpotActivate` 的替换分支（desc = `DescNoMoreBet`）在同一条连锁上
    //      我方已经交过手坑时**不再替换** —— 否则灰流丽刚把原效果无效掉，大姐又把它改回「对方抽 1」，
    //      等于一手白交。
    //
    // v5.22（2026-10-24 第二十三轮修订，replay「_LastReplay.yrp」23:00 局）：未眠之城② 洗牌换了整套判据。
    //   ★ 用户口径：洗回的那张卡**马上会被自己抽回来**，所以目标不是「手里最没用的」，而是「抽回来最赚的」——
    //     · 手牌有 **菈碧** → 洗她到卡组最上面：抽回来 ① 自跳，且 ② 自跳时再抽 1（一手换一怪一抽）；
    //     · 没有菈碧、有 **西艾萝（帐）** → 洗她到最上面：抽回来 ① 自跳，场上多一只 2100/2100；
    //     · 后攻 / T3 要解场 → 洗 **库露雪 / 希耶娜** 到最上面：抽回来 ① 自跳 → ② 弹对方魔陷 / 怪兽；
    //     · 以上都没有 → 把 **手坑放卡组最下面**（洗到顶等于下回合又抽到它，白占一个抽卡位）；
    //     · 手牌有 **卡萝尔** → 别洗她，留着走通召（② 不用解放通召，③ 通召成功抽 1）；
    //       手上只剩她一张可洗时，这条② 干脆不发。
    //   ⇒ 代码：新增 `_returnHandIsCity` / `_returnHandToTop` 两个状态区分「未眠② 可放顶/底」
    //     与「帐② 卡文写死最下面」；新增 `PickCityReturnCard`；`HandCardValue` 里卡萝尔给 90
    //     （比辉煌 80 还靠后，几乎不会被洗回）。
    //   旧行为：两个来源都按 `HandCardValue`（最低分）挑，且一律丢卡组最上面 ——
    //   于是手坑被洗到顶、下回合又摸回来，等于这次② 白转了一圈。
    // ============================================================================
    // ★ 卡组总纲（用户 2026-10-24 口径，优先级高于下面所有具体判据）
    //
    //   升月的卡组逻辑是：与对方资源交换，再通过自身不断抽卡扩大场值和资源优势。
    //   一切操作都要与对方互动才行。
    //
    //   落到代码上的四条判据（新写任何执行器先对照这四条）：
    //   1) 每次操作都要与对方发生互动 —— 打断 / 弹回 / 无效 / 战破 / 夺取 / 削资源都算。
    //      对空场自娱自乐的铺场、加速超量、贴 buff 一律降级或不动，
    //      除非它是下一轮互动的必要铺垫（如群星先置顶，是为了对方回合那一次抽）。
    //   2) 交换必须是赚的：我方付出的资源 < 对方损失的价值。白亏的 cost 一律不发 ——
    //      效果已被无效（EffectNegated）、对方空场（Bounty 的 threat==0）、弹不到东西
    //      （希耶娜/库露雪留手）、素材不足硬叠，都是这一条的具体实现。
    //   3) 抽卡是唯一资源来源：能不花钱就抽，抽到即自跳，
    //      用抽来的资源去换掉对方的资源 —— 把对局带进「对方越动、我方手牌越多」的正循环。
    //   4) 手坑 / 无效只在能换到对方真实资源时交，不因为「手里有就发」，
    //      也不为了「反正免费的」提前浪费掉时点。
    //
    //   判断一手划不划算，问一句：这一手在和对方换什么？换不到就别做。
    // ============================================================================
    [Deck("RaiseMoon", "AI_RaiseMoon", "Normal")]
    class RaiseMoonExecutor : DefaultExecutor
    {
        public class CardId
        {
            // 主卡组·本家
            public const int Hiena = 74212164;      // 刃·希耶娜：抽到自跳 + 弹对手怪兽 / 手牌丢弃抽 1
            public const int Kurusu = 10611873;     // 灯·库露雪：抽到自跳 + 弹对手魔陷 / 手牌丢弃抽 1
            public const int Siero = 75934716;      // 帐·西艾罗：魔法师族，手牌返 1 卡自跳 + 双方主要阶段抽 1
            public const int Carol = 11333460;      // 雅·卡萝尔：可不解放通召 / 抽到自跳 + 抽 1（光属性超量自肃）
            public const int Rabi = 47828069;       // 翼·菈碧：抽到自跳 + 抽 1；对方回合 ③ 加速 R7
            public const int Squiz = 47605577;      // 朔·斯奎兹：手牌丢弃，二选一
            public const int Stars = 46983930;      // 群星：卡组置顶 1 张本家；墓地除外抽 1
            public const int City = 72978038;       // 未眠之城『盈彩月夜』（永续魔法，type 0x020002；落在魔陷区，不是场地区）
            public const int Splendor = 9362643;    // 辉煌（陷阱：从额外特招本家超量 + 自身当素材）

            // 额外·R7
            public const int Jackpot = 73090586;    // 天·西艾萝-一掷乾坤（魔法师族 LIGHT R7，必须 EX 区）
            public const int Bounty = 9484285;      // 望·斯奎兹-千金大奖（机械族 LIGHT R7，斩杀基底）
            public const int Chersib = 94503794;      // 漆梏之食兽（DARK R7×2+，700/2500）：对「通常·速攻魔法」卡组（闪刀 / 光与暗的仪式）
            // ★ v5.11：征霸龙已被换出卡组（用户改用斩杀线上的重铠光子龙拆场地），执行器保留备用 ——
            //   `ExtraDeck.Any` 那关永远不过，所以不会误出。
            public const int BlazeDragon = 47132793;
            public const int ChaosAtlandis = 6387204; // 混沌No.6 大西洲巨人（LIGHT R7×3，3300/3300）：解 ≤3200 攻的抗性怪
            public const int Omega7 = 6195332;        // 托勒密星团Ω7（LIGHT R7×3，2700/2700）：拔 N 弹 N 只怪兽
            public const int Utopia76 = 92015800;     // No.76 谐调光师（LIGHT R7×2，2700/2100）：对墓地卡组
            public const int BigEye = 80117527;       // No.11 巨眼（DARK R7×2，2600/2000）：夺取对方高价值怪兽

            // 额外·斩杀线
            public const int Irisfil = 64626565;    // 黑智天至 伊里斯斐尔 R8 ATK800（①本回合我方超量攻击力 + 自身阶级×100）
            public const int PhotonRoar = 28331069; // 超银河眼光子龙-光子咆哮 R8 ATK4500
            // ★ v5.11 用户换进来的新斩杀层（替换反物质龙）：
            //   银河眼重铠光子龙（LIGHT 龙族 R8，4000/3500）
            //   · 「8星×3」，也能在「银河眼」超量（setcode 0x107b，光子咆哮正是）上面重叠来超量召唤
            //   · ① 把装备卡叠成素材（本卡组没装备，不用）
            //   · ② 1回合1次，拔 1 素材，以对方场上 1 张**表侧表示**的卡为对象 → 破坏（IGNITION，自己主阶）
            //   用户口径：斩杀线走到光子咆哮之后叠它，用它 ② 顺手拆掉对方的场地/表侧魔陷，
            //   这样就不用再占一张额外去当「拆场地」的专职卡了。
            public const int PhotonArmor = 39030163;
            // ★ v5.11 起不在卡组（被重铠替换），执行器保留备用
            public const int Antimatter = 92517928; // 银河眼反物质龙 R9 ATK4000（①战伤减半，②拔 1 可对怪兽攻击 2 次）
            public const int Zeus = 90448279;       // 天霆号 阿宙斯 R12 ATK3000

            // 手坑 / 单卡
            public const int GhostRabbit = 59438930; // 幽鬼兔 ×3：破坏发动效果的那张场上卡
            public const int AshBlossom = 14558127;  // 灰流丽 ×2：无效「检索 / 卡组特招 / 卡组堆墓」
            public const int MaxxC = 23434538;       // 增殖的G ×1
            public const int LockBird = 94145021;    // 小丑与锁鸟 ×1：对方检索后，本回合双方都不能从卡组加手卡
            // ★ v5.37：本卡库里这张牌还有一个**同文异号**的副本（94145022，卡名与卡文与 94145021 完全一致），
            //   对手的构筑有可能带的是那一号 —— 判「这一回合有没有被锁鸟锁住」时必须两个号都认。
            public const int LockBirdAlt = 94145022;
            public const int Veiler = 97268402;      // 效果遮蒙者 ×3
            public const int Burial = 35726888;      // 愚蠢的副葬 ×2：卡组送 1 张魔陷进墓
            public const int InfiniteImpermanence = 10045474; // 无限泡影：盖放发动后整列魔陷被无效
            public const int Jellyfish = 84192580;  // 欢聚友伴·抖抖海月水母：升月从手牌把怪兽特殊召唤时，对方抽2
            public const int Nibiru = 27204311;     // 原始生命态 尼比鲁（陨石）：对方本回合召唤/特招 ≥5 只 → 解放双方前场
        }

        private static readonly int[] ArchetypeL7 = { CardId.Hiena, CardId.Kurusu, CardId.Siero, CardId.Carol, CardId.Rabi, CardId.Squiz };

        // 卡图脚本里的效果描述 ID：aux.Stringid(id, n) = id << 4 | n。
        // 大姐的 ① 有三条独立效果，全靠 desc 区分。
        private const int DescNoMoreBet = (CardId.Jackpot << 4) | 1;   // 替换对手检索/抽卡 → 对方随机抽 1
        private const int DescDrawByCost = (CardId.Jackpot << 4) | 2;  // 拔 1 素材自己抽 1
        private const int DescDrawOnGrave = (CardId.Jackpot << 4) | 3; // 被送去墓地抽 1

        // 帐·西艾罗：①（被抽到时自跳）与 ②（手牌返 1 卡自跳）**都在手牌**，只看位置分不出来。
        // ② 会引发一次「选 1 张卡回卡组最下面」，① 不会 —— 用 desc 精确区分，
        // 否则 ① 会误挂「返手卡」标记，把后面任意一次选卡带偏。
        private const int DescSieroDrawOnDraw = (CardId.Siero << 4) | 0;   // ① 被抽到时自跳
        private const int DescSieroHandSS = (CardId.Siero << 4) | 1;       // ② 手牌返 1 卡自跳
        private const int DescSieroDraw = (CardId.Siero << 4) | 2;         // ③ 场上抽 1

        // 刃 / 灯：手牌上同时有 ①（被抽到时自跳）和 ③（丢弃自身抽 1），只看位置分不出来。
        // c74212164.lua / c10611873.lua 的 SetDescription 分别是 Stringid(id,0) / (id,2)，
        // 用 desc 精确区分 —— 否则「留手牌」的判断会把 ③ 的抽卡也一起禁掉。
        private const int DescHienaSummonOnDraw = (CardId.Hiena << 4) | 0;   // ① 被抽到时自跳
        private const int DescKurusuSummonOnDraw = (CardId.Kurusu << 4) | 0; // ① 被抽到时自跳

        // 反物质龙 ②：拔掉的素材是怪兽时，会问「要不要再从卡组送 1 只同种族怪兽进墓」
        // （脚本里是 Duel.SelectYesNo(tp, aux.Stringid(id,2)) → MSG_SELECT_YESNO，desc 就是这个值）。
        private const int DescAntimatterDump = (CardId.Antimatter << 4) | 2;

        // v5.11：重铠光子龙 ②「拔 1 破坏对方场上 1 张表侧卡」的 desc（c39030163 的 Stringid(id,2)）。
        // ① 「把装备卡叠成素材」是 Stringid(id,1)，本卡组没有装备卡，靠 desc 把两条 IGNITION 分开。
        private const int DescPhotonArmorDestroy = (CardId.PhotonArmor << 4) | 2;

        private bool _lightLock;            // 本回合吃过光属性超量自肃（雅③ / 翼②）→ DARK 终端（漆梏之食兽）不能用
        private int _stackTarget;           // 群星① 置顶：-1 = 待挑，>0 = 指定卡
        private readonly HashSet<int> _starsTopUsed = new HashSet<int>();  // 本回合 ① 已置顶过的卡名（① 不能选同名卡）
        private bool _returnHandPending;    // 接下来会问「返 1 张手卡回卡组」
        // ★ v5.22：返卡的两个来源判据完全不同 ——
        //   · 未眠②（`CityActivate`）可以选放到卡组最上面还是最下面，而那张卡**马上会被自己抽回来**；
        //   · 帐②（`DescSieroHandSS`）卡文写死「回到卡组最下面」，没得选。
        //   所以要记来源，才好决定「挑哪张 + 放哪端」。
            private bool _returnHandIsCity;    // true = 本次是未眠之城② 的洗牌（可放顶/底）
            private bool _returnHandToTop;      // true = 把挑中的卡放到卡组最上面（抽回来触发自跳）
            private bool _cityWaitJackpotLogged; // v5.23：为了等大姐而暂缓② 的日志只打一次
        private int _splendorTarget;        // 辉煌① 要拉的卡号：0 = 未指定（走默认），否则指定 cardId
        private bool _splendorOwnUsed;      // 本回合（自己回合）已用掉辉煌
        private bool _splendorOppUsed;      // 本回合（对方回合）已用掉辉煌
        private bool _splendorGraveUsed;    // 本回合辉煌②（墓地除外抽1）的 1 次名额已用
        private bool _burialPending;        // 愚蠢的副葬：接下来会问「从卡组挑 1 张魔陷送墓」
        private bool _bountyActivated;      // 千金大奖本回合在怪兽区发过效果（会影响黑智天至能否叠）
        private int _bountyBouncePending;   // v5.15：千金大奖 ② 拔 N 弹 N → 还欠几次「选对方的卡」
        // ★ v5.32：通召雅的解放保护。SummonCarol 置位，OnSelectSum 看到它就把解放数答成 0 只
        //   （卡萝尔 ② 可以不用解放作召唤）。v5.44 起真正兜底的是 `SummonCarol` 里
        //   「我方场上没有任何怪兽」的硬闸门，这道 0 只回合作双保险；标记会被一次 OnSelectSum 消费掉，
        //   回合开始再兜底清零。
        private bool _carolSummoning;
        // ★ v5.35：这次通召雅·卡萝尔走的是「不解放」，她的基础攻击力会被改成 700
        //   （c11333460.lua 的 ntop：EFFECT_SET_BASE_ATTACK(700)，RESET_EVENT+0xff0000 → 在场期间一直 700）。
        //   原来 OnSelectPosition 里按卡面 1400 估 SafeToAttack，等于高估一倍，
        //   会把「700 站上去必死」的场面误判成安全。SummonCarol 置位，OnSelectPosition 取用即清。
        private bool _carolNoTribute;
        // ★ v5.32：本次千金大奖是「辉煌 ① 拉出来的」（不是超量召唤）。
        //   它的 ② 走的是「从手卡有怪兽特殊召唤」那一条，专门用来弹卡换资源，
        //   不是斩杀基底 —— 所以抢血模式（RaceMode）的「别打断斩杀链」不该拦住它。
        private bool _bountyFromSplendor;

        // v4.4：欢聚友伴·抖抖海月水母（マルチャミー・プルリア，84192580）对策
        //   ★ 它是**手坑**，不是场上怪兽：从手牌丢弃发动，随后自身进墓，真正生效的是
        //     一条「到本回合结束」的持续效果 —— 效果期间，升月「从手牌把怪兽召唤 / 特殊召唤」
        //     时，对方抽 1。本家六张下级全部「被抽到时从手牌特殊召唤」，一旦踩中就是连续白送。
        //   ★ 所以**不能靠看对方场上有没有怪兽来检测**（旧版正是这样写的，永远命中不了），
        //     只能在连锁建立时（OnChaining）认出对方发动了 84192580 来置位 `_jellyfish`，
        //     到下一个回合开始自动清零（效果 RESET_PHASE+PHASE_END，跨回合即失效）。
        //   三条应对：
        //     · hold 模式：水母生效 + 我方回合 + 手里有陷阱辉煌 + 大姐还没出场
        //       → 本回合压住一切「从手牌的召唤 / 特招」（自跳、通召雅、丢弃抽卡、副葬），
        //         只把陷阱辉煌盖下去，等对方回合用陷阱辉煌从额外卡组直接拉大姐（不从手牌，不喂水母）。
        //     · 无陷阱辉煌 → 照常登 2 只叠大姐（让对面抽 2，自己保留压制），这是默认行为，无需改。
        //     · combo：水母生效 + 手里有群星 + 手里有二速抽卡件（朔7 / 陷阱辉煌）
        //       → 群星优先置顶「对方回合能干扰」的本家（菈碧 / 希耶娜 / 库露雪 / 帐），
        //         等对方回合登场怪兽 / 发动永续魔陷后用二速抽卡抽到并自跳干扰。
        private bool _jellyfish;            // 本回合对方已发动水母（效果未过期）→ 我方从手牌召唤/特招会让对方抽 1
        // hold / combo 一律「实时计算」而不是缓存字段：手牌可能在回合中被抽到 / 用掉，
        // 大姐也可能中途出场，缓存一次就会立刻过期。
        // ★ v5.41 用户口径：「另外中了水母后如果你手牌有辉煌，那么可以直接盖放陷阱（有群星的话置顶照旧），
        //   其他动作一律不做，然后回合结束等对方动作」
        //   ⇒ hold 的判据从「手里有陷阱辉煌 + 大姐未出场」放宽成「手里有陷阱辉煌」（大姐在不在都一样：
        //     有辉煌就有「对方回合一个动作拉出大姐」的路，本回合不必再自己铺）。
        //   ⇒ 一旦进入 hold 就**整回合锁定**（`_setPassTurn`）：辉煌盖下去之后手上就没有它了，
        //     只看手牌会让 hold 立刻失效、bot 接着铺场 / 叠超量，那就白搭了。
        private bool _jellyfishHandSplendor => _jellyfish && Duel.Player == 0
            && Bot.Hand.Any(c => c != null && c.IsCode(CardId.Splendor));
        private bool _holdMode => _jellyfishHandSplendor
            || (_jellyfish && Duel.Player == 0 && _setPassTurn == Duel.Turn);
        private bool _jellyfishCombo => _jellyfish
            && Bot.Hand.Any(c => c != null && c.IsCode(CardId.Stars))
            && (Bot.Hand.Any(c => c != null && c.IsCode(CardId.Squiz))
                || Bot.Hand.Any(c => c != null && c.IsCode(CardId.Splendor))
                // ★ v5.40：大姐已经在场 → 它「自己·对方主要阶段拔 1 素材抽 1」就是现成的对方回合抽卡源，
                //   不再要求手里另外握着一张二速抽卡件（用户口径：置顶之后靠大姐另开连锁抽 1 把怪抽出来）。
                || Bot.GetMonsters().Any(c => c != null && c.IsCode(CardId.Jackpot)));

        // ★ v5.40 用户口径（replay「水母妥协.yrp」）：
        //   「当水母适用之后，升月方应该尽量减少从手牌召唤特殊召唤怪兽的次数，当然要保证对对手的压制。
        //     … 如果场上有 2 只本家了，后续抽到的怪都不要跳到场上 … 这时升月方应该直接叠放大姐。」
        //   水母是「我方每从手牌上 1 只怪 → 对方抽 1」，所以场面一旦够用就停手：
        //     · 大姐已在 EX 区（长线封锁成立，后面靠它自己抽 + 加速）；或
        //     · 主要怪兽区已有 ≥2 只本家 7 星（素材已经够叠大姐，再跳就只是白送抽）。
        //   注意「叠超量 / 从额外卡组特招」不算从手牌上怪，不受这条限制 —— 所以「直接叠大姐」照做。
        private bool _jellyfishBoardSet => _jellyfish
            && (Bot.GetMonsters().Any(c => c != null && c.IsCode(CardId.Jackpot))
                || Bot.GetMonstersInMainZone().Count(c => c != null && c.IsFaceup()
                    && ArchetypeL7.Contains(c.Id) && !c.HasType(CardType.Xyz)) >= 2);
        // 停铺闸门 = 原 hold 模式 ∪ 场面已够用。两套条件互不重叠：
        //   hold 是「大姐还没出、手里有陷阱辉煌」；boardSet 是「大姐已出 / 已铺够 2 只本家」。
        private bool _jellyfishFreeze => _holdMode || _jellyfishBoardSet;
        private string JellyfishFreezeWhy()
        {
            if (_holdMode) return "（手牌有陷阱辉煌 → 只盖它（有群星则置顶），其余动作一律不做，等对方回合用辉煌拉大姐）";
            if (_jellyfishBoardSet) return "（大姐已在场 / 本家已有 2 只 → 再跳只是送对方抽 1）";
            return "";
        }

        // ★ v5.40：水母 combo 已经把一张本家压在牌堆顶（群星① 置顶菈碧 / 希耶娜 / 库露雪）——
        //   本回合剩下的抽卡全部让位，把这张**留给对方回合那一次「大姐另开连锁抽 1」**抽出来，
        //   抽到手就是「被抽到时自跳」→ 落地直接解场 / 干扰，而不是本回合再喂水母一张。
        //   录像 02:45 的病根：02:45:54 置顶菈碧 → 02:45:56 群星② 立刻把它抽走 → 02:45:57 菈碧本回合自跳，
        //   置顶那一步等于白做，还多送对方 1 张。
        private bool _comboTopHeld;
        private bool JellyfishHoldingTop()
        {
            return _jellyfishCombo && _comboTopHeld;
        }

        // ★ v5.41：水母 hold 的「盖辉煌就过」状态。
        //   `_setPassTurn` = 本回合已经选了这条线（盖完辉煌手牌就空了，靠回合号把 hold 锁住整回合）。
        //   `_holdPassLogged` = 这条线的日志一回合只打一次（idle 循环里每个执行器都会被问）。
        private int _setPassTurn = -1;
        private bool _holdPassLogged;
        // ★ v5.41：hold 时**唯一放行**的两个执行器在列表里的下标 ——
        //   群星①（置顶，用户口径「有群星的话置顶照旧」）与辉煌的 SpellSet（盖放陷阱）。
        //   为什么用下标而不是 (Type, CardId) 匹配：群星① 与群星② 是同一个 (Activate, 群星)，
        //   只看卡号分不开；而 CardExecutor 会把 cardId 过一遍 NamedCard 别名解析，比卡号也不可靠。
        private int _starsTopExecIndex = -1;
        private int _splendorSetExecIndex = -1;
        // ★ v5.41：「盖完就结束回合」那条 GoToEndPhase 执行器的下标（v5.42 起挪到列表**最后一条**）。
        private int _holdEndExecIndex = -1;
        // ★ v5.42 用户口径（对 v5.41「其他动作一律不做」的修正）：
        //   「如果手牌有辉煌还是尽量不要从手牌召唤特殊召唤怪兽吧（西艾萝除外，她特殊召唤到场上可以抽 1，
        //     对方回合还能抽 1），其他动作的话除了群星置顶，副葬可以继续堆墓辉煌（但是不抽卡），
        //     未眠之城可以把手牌的本家怪兽回卡组顶以达到下回合干扰效果」
        //   ⇒ hold 从「全封」放宽成**白名单**：这三个动作（副葬 / 未眠② / 帐自跳）额外放行，
        //     其余仍然一律不做（尤其是「从手牌上别的本家怪」和「叠超量」）。
        //   存的是 `Executor.Executors` 里的下标 —— 与 `_starsTopExecIndex` 同一套机制：
        //   CardExecutor 的 (Type, CardId) 分不开同一个卡号的不同效果（副葬有两条注册、
        //   帐的 ①②③ 共用一个执行器），只有下标能精确定位。
        private readonly List<int> _holdExtraWhitelist = new List<int>();

        // 水母 hold 的收尾（执行器列表里**最后一条**）：手里没辉煌可盖了、群星也置顶完了 → 直接结束回合。
        // ★ v5.42：因为排在最后一条，副葬 / 未眠② / 帐 这些白名单动作在这一轮已经先被问过了，
        //   走到这里还判 true 就说明「能做的都做完了」。
        private bool JellyfishHoldEndTurn()
        {
            if (!_holdMode) return false;
            if (_jellyfishHandSplendor) return false;                       // 手里还有辉煌没盖 → 先盖它
            if (Bot.Hand.Any(c => c != null && c.IsCode(CardId.Stars)) && !_starsToppedThisTurn)
                return false;                                              // 群星① 还没置顶 → 先置顶
            Log("水母 hold → 辉煌已盖好（群星 / 副葬 / 未眠② / 帐 该做的也做完了），结束回合等对方动作");
            return true;
        }

        // hold 期间「其余动作一律不做」的闸门：连 Activate 带 SpSummon / Summon / Repos / SpellSet 一起挡
        //（白名单那几条在包装时就已经豁免，不会走到这里）。
        private bool JellyfishStopAllActions()
        {
            if (!_holdMode) return false;
            if (!_holdPassLogged)
            {
                _holdPassLogged = true;
                bool hasStars = Bot.Hand.Any(c => c != null && c.IsCode(CardId.Stars));
                bool hasBurial = Bot.Hand.Any(c => c != null && c.IsCode(CardId.Burial));
                bool cityInHand = Bot.Hand.Any(c => c != null && c.IsCode(CardId.City));
                bool cityOnField = Bot.GetSpells().Any(c => c != null && c.IsCode(CardId.City));
                Log("水母 hold → 只做「盖辉煌" + (hasStars ? " + 群星①置顶" : "")
                    + (hasBurial ? " + 副葬堆墓（不抽）" : "")
                    + (cityOnField || (cityInHand && !hasBurial) ? " + 未眠②回卡组顶" : "")
                    + (cityInHand && hasBurial ? " + 未眠留手牌（③ 会把副葬堆的辉煌洗回卡组）" : "")
                    + (Bot.Hand.Any(c => c != null && c.IsCode(CardId.Siero)) ? " + 帐（西艾萝）自跳" : "")
                    + "」，其余一律不动 → 过回合等对方动作");
            }
            return true;
        }

        // ★ v4.8：对方是否露出了「吃墓地」的马脚（从墓地发动效果 / 从墓地特招）。
        //   这是 No.76 谐调光师能不能出的**硬证据**，整局保持（一旦暴露就一直是墓地卡组）。
        //   原来只看「对方墓地 ≥3 只怪兽」—— 但对面随手丢 3 张手坑进墓也会满足，
        //   于是先攻刚开局就把两只 7 星烧成一只 2700 白板，而对面根本不靠墓地。
        private bool _enemyGravePlay;

        // 手坑：它们进墓地只说明「对面交了手坑」，不代表对面是墓地卡组，统计时要排除。
        private static readonly int[] HandTrapIds =
        {
            CardId.AshBlossom, CardId.GhostRabbit, CardId.Veiler,
            CardId.MaxxC, CardId.LockBird, CardId.Jellyfish,
            CardId.Nibiru,   // v5.13：陨石也是手坑 —— No.76 数对方墓地时要排除，翼③ 也不能拿它当「对方要特招」的证据
        };

        // 针对性超量在「是否发动」时算好的目标，紧接着的选卡询问直接照它选。
        // 引擎只会送一个 hint，光看 hint 分不出「抢怪 / 装备 / 弹回卡组 / 吸墓」，只能自己记。
        private ClientCard _stealTarget;         // 巨眼①：要夺取控制权的对方怪兽
        private ClientCard _chaosTarget;         // 混沌大西洲①：要装备过来的对方怪兽
        private List<ClientCard> _omegaTargets;  // 托勒密②：要弹回卡组的对方怪兽
        private bool _utopiaPending;             // No.76③：接下来会问「对方墓地挑 1 只怪兽」
        private ClientCard _utopiaTarget;        // No.76③：决策时点名要吸的那只（选卡询问照它选）
        private ClientCard _veilerTarget;        // 效果遮蒙者：要无效的那只（= 对方刚发动效果的那只场上怪）
        // ★ v5.5：希耶娜 / 库露雪 ②「弹回卡组最下面」要弹的那张。
        //   引擎的选卡询问不区分「弹怪 / 弹魔陷」，默认逻辑会随手挑一张，
        //   所以要像巨眼 / 遮蒙者那样，在决定发动的当下就把目标定下来。
        private ClientCard _bounceTarget;
        // ★ v5.10：征霸龙 ① 的两次选卡（先「自己 1 张」，再「对方 1 张」）。
        //   引擎分两次问、hint 都是破坏选卡，所以必须自己记：第 1 次挑自己那张、第 2 次挑对方那张。
        private ClientCard _blazeSacrifice;      // 要破坏的「自己」那张（首选 EX 区免疫的大姐 = 白嫖）
        private ClientCard _blazePrey;           // 要破坏的「对方」那张（场地 / 表侧永续优先）
        private int _blazePending;               // 还剩几次选卡要接管：2 = 自己的，1 = 对方的
        // ★ v5.11：重铠光子龙 ② 要破坏的那张（对方场上表侧卡），发动时点名，选卡时照选。
        private ClientCard _armorTarget;
        // ★ v5.27：重铠 ② 最后一次发动是第几回合（`c39030163.lua` 的 e2 带 `SetCountLimit(1)` = 同名卡
        //   一回合一次）。天霆号原来只看「对方还有可拆的目标」就让位等重铠，拆完第一张后目标换成第二张，
        //   于是永远在等一个本回合已经发不出来的效果 —— 天霆号整回合叠不上去（replay 23:32:57/58 连等两次）。
        private int _armorUsedTurn = -1;
        private int _enemyLastSummonedId;        // 对方最后登场的怪兽 id（提前交遮蒙者时优先它）
        private int _enemyLastSummonedTurn;      // 上面那只是第几回合登场的
        // ★ v5.9：v5.5 的 `_rabiArrivedTurn`（记菈碧落场回合，用来认 ② 抽 1）已删除 ——
        //   ② 是诱发效果、走 MSG_SELECT_EFFECTYN，用 `AI.SelectingEffectYn` 就能精确分流，
        //   不需要再靠「本回合刚落地」这种间接判据。
        private bool _descProbeLogged;           // v5.5：后攻首回合打一次手牌 desc，确认 ① 的编码

        // ★ v5.2 陨石：对方本回合「召唤·特殊召唤成功」的累计次数。
        //   陨石的发动条件就是这一条（≥5 只），引擎不提供现成计数，只能自己在 OnMove 里数。
        private int _enemySummonCount;
        private bool _nibiruUsed;                // 本回合陨石已发动（卡名 1 回合 1 次）
        // ★ v5.38：陨石「等对方把怪摆到场上」的两个状态 ——
        //   `_nibiruSeenCount` = 上一次来问的时候对方的召唤/特招累计数（没变 = 对方这一波停了，可以开）；
        //   `_nibiruWaitLogged` = 本回合已经打过一条等待日志（同一回合的时点会被反复询问，别刷屏）。
        private int _nibiruSeenCount;
        private bool _nibiruWaitLogged;
        // ★ v5.20：灰流丽本回合已经交出去的回合号（-1 = 没交过）。
        //   灰流丽 1 回合 1 次，朔靠这条判断「对方这一次检索该不该让给灰流丽」——
        //   没交过 → 让；交过了 → 对方再来检索就轮到朔挂持续抽卡。
        private int _ashUsedTurn = -1;

        // ★ v5.29：上一次「进连锁的卡」是谁的（0 = 我方，1 = 对方，-1 = 本回合还没人发动）。
        //   `Util.GetLastChainCard()` 在连锁结算完就空了，而锁鸟这类**延迟诱发**（EFFECT_FLAG_DELAY）
        //   是在整条连锁结算完之后才问我们 —— 那时连锁已经清空，只能靠这里记的「最后一手是谁」。
        private int _lastChainController = -1;

        // ★ v5.29：大姐刚把对方这条检索换成「对方随机抽 1」。
        //   那个抽卡是 `Duel.Draw(1-tp,1)`，照样会 raise `EVENT_TO_HAND`（prev = 卡组），
        //   锁鸟的 lua 就是靠这个事件跳出手的 —— 但它接的是**我方大姐的结算**，不是对方自己的动作。
        //   置位时机：`JackpotActivate` 的替换分支返回 true 那一刻；被锁鸟消费/换回合时清掉。
        private bool _jackpotReplacedSearch;

        // ★ v5.36 用户口径：「手卡有朔、场上有大姐，对方发动检索效果时得分情况」——
        //   手牌很少（`SquizThinHand` 张以内，含手里这张朔）→ 朔挂「持续抽卡」；
        //   其余情况 → 大姐置换检索，朔留着以后另开 C1 抽 1。
        //   本字段记「这一回合朔已经挂上持续抽卡了」，让大姐在同一回合的检索上让位
        //   （两条路二选一：大姐一替换，对方就不再「从卡组加入手卡」，朔那一次的抽 1 也触发不了）。
        private bool _squizContinuous;

        // 手牌「很少」的阈值：≤2 张（含朔本人）才算断粮。3 张以上还有下一步可走，
        // 这时候把朔换成「对方每次检索抽 1」不如让大姐直接把对方这次检索掐掉。
        private const int SquizThinHand = 2;

        // ★ v5.37 用户口径：「当升月中了小丑与锁鸟效果适用的时候，未眠之城还是留在手牌不要发动吧。」
        //   锁鸟的 operation（c94145021.lua）除了 `EFFECT_CANNOT_TO_HAND` 还挂了 `EFFECT_CANNOT_DRAW`
        //   （双方 / RESET_PHASE+PHASE_END），而未眠② 的 ●1 / ●2 在 `s.drtg` 里都以
        //   `Duel.IsPlayerCanDraw(tp,1)` 为前提 —— 锁鸟回合里这张永续魔法完全是死牌。
        //   ⇒ 整回合不动它：手上那张不铺、场上那张不发 ②。
        //   只按**回合号**判（`_drollTurn == Duel.Turn`），因为锁鸟的封锁到回合结束就复位。
        private int _drollTurn = -1;        // 小丑与锁鸟生效的回合号（-1 = 还没出现过）
        private bool _cityDrollLogged;      // 未眠被锁鸟拦下的日志一回合只打一次
        private bool _cityBurialLogged;     // ★ v5.43：未眠给副葬让路的日志一回合只打一次

        // ★ v5.2 副葬 / 群星的顺序：先副葬堆辉煌（洗过牌）→ 再群星置顶 → 之后才允许抽卡。
        private bool _burialUsedThisTurn;        // 本回合副葬已发动
        private bool _starsToppedThisTurn;       // 本回合群星① 已置顶
        private bool _orderHoldLogged;           // 顺序闸门的日志只打一次，避免每次 idle 刷屏

        public RaiseMoonExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            // ★ v5.42：`ExecutorType.GoToEndPhase` 从「第 0 位」挪到**构造末尾的最后一条**。
            //   为什么改：v5.41 把它放第 0 位时，`OnSelectIdleCmd` 一进循环就先问它，
            //   一旦判 true 立刻结束回合 —— 而 v5.42 的 hold 白名单（副葬堆墓 / 未眠②回卡组顶 /
            //   帐自跳）都是排在后面的执行器，永远轮不到。
            //   放最后同样能挡掉兜底的「有攻击表示的怪就进战阶」：`OnSelectIdleCmd` 在循环内
            //   每遇到一条 GoToEndPhase 都会 `return ToEndPhase`，走到最后一条时循环还没结束，
            //   兜底分支根本执行不到。
            //   （细节见末尾「===== 13. =====」那一段。）

            // ===== 0. 主要怪兽区空间闸门（v5.7）=====
            // ★ 必须是列表里**最前面两条**。抽卡效果全是 ExecutorType.Activate（注册在下面第 1~3 段），
            //   而 OnSelectIdleCmd 是按执行器顺序遍历的 —— 只有排在它们前面，才能在
            //   「这一波抽卡开始之前」先把场上的 7 星叠掉腾格子。理由详见下方「主要怪兽区空间闸门」段。
            // ★ ShouldExecute 里 `exec.Type == type` 是硬比较，这两条只在 SpSummon 这一路被调用，
            //   不会抢掉千金大奖 / 大姐自己的 Activate。
            AddExecutor(ExecutorType.SpSummon, CardId.Jackpot, CompactToExtraZone);  // 2 只 7 星 → 大姐落 EX 区（空出 2 格）
            AddExecutor(ExecutorType.SpSummon, CardId.Bounty, CompactToMainZone);    // 2 只 7 星 → 千金大奖（空出 1 格）

            // ===== 0.5 先攻首回合：第一个动作先通召雅（v5.34）=====
            // ★ 用户口径：「如果升月先攻有卡萝尔，尽量第一个动作先直接召唤卡萝尔抽卡。」
            //   ③ 抽 1 是本卡组唯一不花钱的续航，越早抽越早把本家抽上来接着自跳铺场；
            //   先攻首回合的自肃（本回合不能再从卡组加入手卡 + 只能光属性超量）在本卡组里代价为零
            //   —— 没有检索卡，额外全是光属性。其它回合仍走第 3.1 段那条排在
            //   「副葬 / 群星 / 场地」之后的注册，保持 v5.15 的抽卡顺序。
            AddExecutor(ExecutorType.Summon, CardId.Carol, SummonCarolFirst);

            // ===== 1. 手牌引擎：所有本家效果一律发动 =====
            // 引擎判断「要不要发动效果」时只看 ExecutorType.Activate（GameAI.OnSelectEffectYn）；
            // 没注册就等于永远回答"不发动"。
            AddExecutor(ExecutorType.Activate, CardId.Hiena, DiscardOrBounce);    // 手牌③丢弃抽1 / 场上②弹怪
            AddExecutor(ExecutorType.Activate, CardId.Kurusu, DiscardOrBounce);   // 手牌③丢弃抽1 / 场上②弹魔陷
            // ★ v5.42：帐（西艾萝）记进 hold 白名单 —— 用户口径「西艾萝除外，她特殊召唤到场上可以抽 1，
            //   对方回合还能抽 1」。她 ② 从手牌自跳虽然也会让对方抽 1（水母），但她 ③「双方主要阶段抽 1」
            //   本回合 + 对方回合各能赚一张，而且场上多一只 2100/2100，这笔账是正的就照跳。
            _holdExtraWhitelist.Add(Executors.Count);
            AddExecutor(ExecutorType.Activate, CardId.Siero, SieroActivate);
            AddExecutor(ExecutorType.Activate, CardId.Carol, CarolActivate);
            AddExecutor(ExecutorType.Activate, CardId.Rabi, RabiActivate);
            // ★ v5.20：朔 的注册从这一段**挪到第 9 段手坑之后**。
            //   原因：`GameAI.OnSelectChain` 是「外层遍历 Executor.Executors（注册顺序）、内层遍历可选卡」，
            //   注册在前 = 绝对优先。朔 一旦排在手坑前面，对方发起检索时它会先抢下 C2，
            //   链上最后一环变成我方的卡 → 灰流丽 / G 当场的闸门（`last.Controller == 1`）全部不成立。
            //   用户口径：**先灰流丽无效对方第一个检索，对方再开第二个检索才轮到朔**。
            //   所以朔必须排在灰流丽 / G 之后（见第 9 段末尾）。

            // ===== 2. 通召：雅是 Lv7 里唯一能不用解放上场的 =====
            // ★ 硬性限制：场上本家怪兽 < 2 时才允许通召。
            //   雅 ② 是「可以不用解放作召唤」，引擎给的是 min=0 的解放选择；
            //   只要自己场上已经站着 2 只 7 星，一解放就等于拿两只 R7 素材换一只 1400 白板。
            //   卡这个条件之后，2 只以上时引擎根本不会把她算成可解放召唤 —— 绝对不可能再吃掉场面。
            // ★ v5.15 用户口径（优化5）：**通召雅这件事要排在本家魔陷的抽卡效果之后** ——
            //   见下方 ===== 3.1 处（注册位置在 CityActivate 之后）。

            // ===== 3. 本家魔陷 =====
            // ★ 群星必须拆成两个执行器，且「②墓地除外抽1」排在「①置顶」前面。
            //   GameAI.OnSelectIdleCmd 的外层循环是执行器、内层才是可发动卡：
            //   ② 排前面 → 只要墓地里躺着群星，它就会先被抢着发掉，然后才轮到另一张 ①。
            //   顺序因此变成 ①A → ②A → ①B → ②B（正确的两拍）。
            //   以前合成一个执行器时是 ①A → ①B → ②A → ②B，两张卡都压在最上面，
            //   中间随便来个洗牌（未眠② / 帐② / 对手洗切）就把置顶卡洗走了，等于白置顶。
            // ★ v5.2 顺序：副葬必须排在群星① 之前。
            //   副葬（从卡组选 1 张魔陷送墓）会让卡组重洗。先置顶再副葬 = 置顶的那张被洗走；
            //   先副葬（洗牌落定）再置顶，置顶才真的压在牌堆顶。副葬因此提到群星前面。
            // ★ v5.42：副葬记进 hold 白名单 —— 用户口径「副葬可以继续堆墓辉煌（但是不抽卡）」。
            //   副葬本身不从手牌上怪（只从卡组送墓），不喂水母；堆下去的辉煌这回合不抽
            //   （辉煌② 那条执行器仍被 hold 闸门挡着），纯粹为下回合/后续留墓。
            _holdExtraWhitelist.Add(Executors.Count);
            AddExecutor(ExecutorType.Activate, CardId.Burial, BurialActivate);

            AddExecutor(ExecutorType.Activate, CardId.Stars, StarsBanish);
            // ★ v5.41：记下「群星①置顶」这条执行器的下标 —— 水母 hold 时要单独放行它（② 仍然挡）。
            _starsTopExecIndex = Executors.Count;
            AddExecutor(ExecutorType.Activate, CardId.Stars, StarsTop);
            // ★ v5.42：未眠之城记进 hold 白名单 —— 用户口径「未眠之城可以把手牌的本家怪兽回卡组顶，
            //   以达到下回合干扰效果」。整条执行器放行（铺卡 + ②回1抽1），锁鸟闸门在方法内部照旧生效。
            _holdExtraWhitelist.Add(Executors.Count);
            AddExecutor(ExecutorType.Activate, CardId.City, CityActivate);

            // ===== 3.1 通召雅（v5.15 下移到这里）=====
            // ★ 用户口径（优化5）：「手卡有卡萝尔和其他抽卡部件时，先不（为了抽卡）作免解放通召，
            //   先把其他抽卡效果发完，再通召。」
            //   为什么是「挪注册位置」就够：
            //     OnSelectIdleCmd 的外层循环是**执行器列表顺序**，每个执行器依次尝试
            //     Activate → MonsterSet → Repos → SpSummon → Summon → SpellSet。
            //     原来这条排在副葬 / 群星 / 场地 之前 → 引擎一问「要不要通召雅」就先把她放上场、
            //     吃掉 ③ 的抽 1，副葬/群星/场地 的那几次抽卡只能排在她后面结算；
            //     下移到这里后，只要场上还有可发的副葬 / 群星① / 群星② / 未眠②，
            //     它们会被先问、先结算，雅的通召自然落到最后。
            //   ★ 不改成「手牌里还有抽卡部件就 return false」的写法：那种闸门在
            //     「群星在手里但已经置顶过 / 副葬在手里但辉煌②名额已用」时会永远不满足，
            //     雅就被永久压死在手牌里，等于白白少一只 1400 的身板和一次抽卡。
            AddExecutor(ExecutorType.Summon, CardId.Carol, SummonCarol);

            // ★ 愚蠢的副葬是新构筑里唯一的「主动起手」：把卡组里的辉煌送墓 → 辉煌② 除外抽 1。
            //   本卡组没有检索，这相当于凭空多一个 1 换 1 的启动点，抽到的 Lv7 还能自跳接着铺。
            _holdExtraWhitelist.Add(Executors.Count);   // v5.42：第二条副葬注册同样进 hold 白名单
            AddExecutor(ExecutorType.Activate, CardId.Burial, BurialActivate);

            // ===== 4. EX 区长线：大姐（只有 EX 区才有全抗 / 检索转换）=====
            AddExecutor(ExecutorType.SpSummon, CardId.Jackpot, XyzJackpot);

            // ===== 5. 主要怪兽区：先给「针对性终端」机会，条件不成立才落到千金大奖 =====
            // ★ 顺序就是优先级 —— GameAI 按注册顺序遍历，第一个说「做」的执行器直接生效。
            //   这几只都用了比千金大奖更苛刻的条件（素材数量 / 对手场面类型），
            //   所以正常局面它们会自己让路，不会抢掉千金大奖的位置。
            AddExecutor(ExecutorType.SpSummon, CardId.ChaosAtlandis, XyzChaosAtlandis); // R7×3：碾抗性大怪（当回合不能打人）
            AddExecutor(ExecutorType.SpSummon, CardId.BigEye, XyzBigEye);               // R7×2：抢对方高价值怪兽
            AddExecutor(ExecutorType.SpSummon, CardId.Omega7, XyzOmega7);               // R7×3：解场弹 3 / M2 压制
            AddExecutor(ExecutorType.SpSummon, CardId.Utopia76, XyzUtopia76);           // R7×2：挖对方墓地
            // ★ v5.10：征霸龙排在千金大奖之前 —— 它的条件（对方场上有一张值得拆的表侧魔陷/场地）成立时优先，
            //   不成立就自动让路给千金大奖。前面四只（碾怪 / 抢怪 / 弹怪 / 挖墓）都只处理怪兽，
            //   和它不冲突：它管的是「后场的魔法陷阱 + 场地」。
            AddExecutor(ExecutorType.SpSummon, CardId.BlazeDragon, XyzBlazeDragon);     // R7×2：拔1 破坏对方1张（场地/永续）
            AddExecutor(ExecutorType.SpSummon, CardId.Chersib, XyzChersib);             // R7×2：对魔法卡组 / 千金大奖用尽（已不在卡组）
            AddExecutor(ExecutorType.SpSummon, CardId.Bounty, XyzBounty);               // 默认基底

            // ===== 6. 斩杀线：千金大奖 → 黑智天至 → 光子咆哮 → 反物质龙 → 天霆号 =====
            // ★ 注册顺序就是叠放顺序（M2 时 Antimatter 先补一层，Zeus 再叠上去）。
            AddExecutor(ExecutorType.SpSummon, CardId.Irisfil, IrisfilSummon);        // R7 → R8 黑智天至 800（中转）
            AddExecutor(ExecutorType.SpSummon, CardId.PhotonRoar, PhotonRoarSummon);  // R8 → R8 光子咆哮 4500（主打点）
            AddExecutor(ExecutorType.SpSummon, CardId.Antimatter, AntimatterSummon);  // → R9 反物质龙（v5.11 起不在卡组，保留备用）
            AddExecutor(ExecutorType.SpSummon, CardId.PhotonArmor, PhotonArmorSummon); // v5.11 → R8 重铠（拔1 破坏对方1张表侧卡）
            AddExecutor(ExecutorType.SpSummon, CardId.Zeus, ZeusSummon);              // → R12 天霆号（下个对方回合拔 2 清场）

            // ===== 7. 陷阱 =====
            // ★ v5.41：记下「盖辉煌」这条执行器的下标 —— 水母 hold 时它是除群星① 之外唯一放行的动作。
            _splendorSetExecIndex = Executors.Count;
            AddExecutor(ExecutorType.SpellSet, CardId.Splendor, ShouldSetSplendor);
            AddExecutor(ExecutorType.Activate, CardId.Splendor, SplendorActivate);

            // ===== 8. 额外怪兽自己的效果（不注册 = 永不发动）=====
            AddExecutor(ExecutorType.Activate, CardId.Jackpot, JackpotActivate);
            AddExecutor(ExecutorType.Activate, CardId.Bounty, BountyActivate);
            AddExecutor(ExecutorType.Activate, CardId.BigEye, BigEyeActivate);
            AddExecutor(ExecutorType.Activate, CardId.ChaosAtlandis, ChaosAtlandisActivate);
            AddExecutor(ExecutorType.Activate, CardId.Omega7, OmegaActivate);
            AddExecutor(ExecutorType.Activate, CardId.Utopia76, Utopia76Activate);
            AddExecutor(ExecutorType.Activate, CardId.Chersib, ChersibActivate);
            AddExecutor(ExecutorType.Activate, CardId.BlazeDragon, BlazeActivate);   // v5.10：① 拔1 → 自己+对方各破坏 1 张
            AddExecutor(ExecutorType.Activate, CardId.Irisfil, () => true);      // ①超量召唤成功 → 全队加攻
            AddExecutor(ExecutorType.Activate, CardId.PhotonRoar, PhotonRoarActivate); // ①登场拉光子；②无效全场一律不开
            AddExecutor(ExecutorType.Activate, CardId.Antimatter, AntimatterActivate);
            AddExecutor(ExecutorType.Activate, CardId.PhotonArmor, PhotonArmorActivate); // v5.11：② 拔1 破坏对方 1 张表侧卡
            AddExecutor(ExecutorType.Activate, CardId.Zeus, ZeusActivate);

            // ===== 9. 手坑：只干扰对面，绝不对着自己的效果交 =====
            // ★ v5.20 顺序重排（注册顺序 = 同一时点的绝对优先级）：
            //   灰流丽 → 幽鬼兔 → 遮蒙者（三张单点无效，`UseNegate` 保证同一条连锁只交 1 张）
            //   → 陨石（1 回合 1 次的清场，比持续型疗效稀缺）
            //   → 锁鸟（后攻 T1 掐断对方整条展开）
            //   → 增殖的G（持续抽卡，最后）
            //   → 朔（本家持续抽卡，排在手坑之后：对方检索这一环永远先给灰流丽 / G，剩下的检索才轮到它）
            //   旧顺序是 G 在灰流丽之前、朔在最前面 —— 朔 / G 一抢链，后面的无效系全部发不出。
            AddExecutor(ExecutorType.Activate, CardId.AshBlossom, UseAshBlossom);
            AddExecutor(ExecutorType.Activate, CardId.GhostRabbit, UseGhostRabbit);
            AddExecutor(ExecutorType.Activate, CardId.Veiler, UseVeiler);
            AddExecutor(ExecutorType.Activate, CardId.Nibiru, NibiruActivate);   // 陨石（v5.2）
            AddExecutor(ExecutorType.Activate, CardId.LockBird, UseLockBird);
            AddExecutor(ExecutorType.Activate, CardId.MaxxC, UseMaxxC);
            AddExecutor(ExecutorType.Activate, CardId.Squiz, SquizActivate);

            // ===== 9.5 手坑盖伏苟命（v3.8）=====
            // 手里一张本家怪都没有（展不开）+ 对方场攻已经够直接打死我 → 把手坑盖到怪兽区挡刀。
            // 盖着的怪被直接攻击只会战破、不受战斗伤害 = 一只盖怪挡一次直接攻击。
            AddExecutor(ExecutorType.MonsterSet, CardId.LockBird, SetHandTrapAsShield);
            AddExecutor(ExecutorType.MonsterSet, CardId.Veiler, SetHandTrapAsShield);
            AddExecutor(ExecutorType.MonsterSet, CardId.GhostRabbit, SetHandTrapAsShield);
            AddExecutor(ExecutorType.MonsterSet, CardId.AshBlossom, SetHandTrapAsShield);
            AddExecutor(ExecutorType.MonsterSet, CardId.MaxxC, SetHandTrapAsShield);

            // ===== 10. 进攻：能打死就转攻、能收就进战阶 =====
            // ★ 没有这一条的话，两只 R7 全是守备表示，ClientField.HasAttackingMonster() 为 false，
            //   GameAI.OnSelectIdleCmd 结尾的兜底会直接「结束回合」，整回合一次都不打。
            AddExecutor(ExecutorType.Repos, CardId.Bounty, ReposToAttack);
            AddExecutor(ExecutorType.Repos, CardId.Jackpot, ReposToAttack);
            // v4.6：低攻高守的本家下级（翼·菈碧 700/2100、朔·斯奎兹 700/1400）兜底翻守备
            // v5.4：任意「低攻守备怪」只要打得死对方一只高价值怪兽（卡组引擎 / 额外怪 / 2000+ 打点），
            //       自己回合主阶 1 就翻攻击去换掉它。必须排在 ReposLowAtkToDefence **之前**：
            //       那条负责把低攻怪翻守备，这条负责在有目标时翻攻，顺序反了会互相抵消、白耗主阶。
            AddExecutor(ExecutorType.Repos, ReposTradeUp);
            // ★ v5.30：对方前场空着 → 本家下级从守备翻攻击去打脸（空场面前躺着等于白躺）
            AddExecutor(ExecutorType.Repos, ReposPressEmptyField);
            AddExecutor(ExecutorType.Repos, CardId.Rabi, ReposLowAtkToDefence);
            AddExecutor(ExecutorType.Repos, CardId.Squiz, ReposLowAtkToDefence);
            // v5.3：天霆号 3000/3000 守备出场的，下个自己回合要翻攻击打伤害
            AddExecutor(ExecutorType.Repos, CardId.Zeus, ReposZeus);
            AddExecutor(ExecutorType.GoToBattlePhase, GoToBattle);

            // ===== 11. 兜底 =====
            AddExecutor(ExecutorType.Repos, DefaultMonsterRepos);
            AddExecutor(ExecutorType.SpellSet, DefaultSpellSet);

            // 群体清场检测要靠读 lua 脚本，先把解析到的目录记一笔：
            // 如果这里是 null / 路径不对，说明 Bot 不是从 ygopro\WindBot\ 下启动的，
            // 用环境变量 SHENGYUE_SCRIPT_DIR 指定即可（否则「破坏场上所有卡」识别不出来）。
            Log("脚本目录：" + (RaiseMoonScriptWipe.ResolvedDir ?? "<未找到>"));

            // ===== 12. 水母 hold：除「盖辉煌 / 群星①置顶 / 结束回合」之外一律不动（v5.41）=====
            // ★ 为什么在构造末尾统一包装，而不是在每个执行器里各加一行 `if (_holdMode) return false;`：
            //   `GameAI.OnSelectIdleCmd` 是「按执行器顺序 × 可动卡」逐个问，**任何一个 Func 返回 true 就立刻执行**，
            //   所以「一律不做」这件事必须把**每一条**都挡住。逐个方法改有两个毛病：
            //     ① 现在 40 多条执行器，漏一条就等于漏一个动作，以后新加执行器还会继续漏；
            //     ② 挡不住 SpSummon（叠超量）—— 它不从手牌上怪、不喂水母，但用户口径要的是
            //        「盖完辉煌就过回合」，本回合的场面动作也不该做（对方回合用辉煌拉大姐更省）。
            //   这里把 Func 统一包一层：hold 期间一律 false，只放过白名单里的下标
            //   （盖辉煌 / 群星①置顶 / 副葬 / 未眠② / 帐 / 结束回合，见 `_holdExtraWhitelist`）。

            // ===== 13. 水母 hold 的收尾：放到**最后一条**（v5.42）=====
            // ★ 必须排在所有白名单执行器之后：`OnSelectIdleCmd` 是从前往后逐条问的，
            //   放第 0 位时「盖完辉煌就立刻结束回合」，副葬 / 未眠② / 帐 永远轮不到（v5.41 的漏洞）。
            //   放最后一条同样能挡掉兜底的「有攻击表示的怪 → 进战阶」：循环走到这里还没 return 过，
            //   `main.CanEndPhase && Func()` 判 true 就直接 ToEndPhase 返回，兜底分支根本执行不到。
            _holdEndExecIndex = Executors.Count;
            AddExecutor(ExecutorType.GoToEndPhase, JellyfishHoldEndTurn);

            InstallJellyfishHoldGate();
        }

        // 给除白名单（盖辉煌 / 群星①置顶 / 副葬 / 未眠② / 帐 / 结束回合）之外的每个执行器
        // 套上水母 hold 闸门（见构造函数第 12 段说明）。
        private void InstallJellyfishHoldGate()
        {
            for (int i = 0; i < Executors.Count; i++)
            {
                if (i == _starsTopExecIndex || i == _splendorSetExecIndex || i == _holdEndExecIndex) continue;
                if (_holdExtraWhitelist.Contains(i)) continue;   // ★ v5.42：副葬 / 未眠 / 帐
                CardExecutor exec = Executors[i];
                Func<bool> inner = exec.Func;
                Executors[i] = new CardExecutor(exec.Type, exec.CardId, () =>
                {
                    if (JellyfishStopAllActions()) return false;
                    return inner == null || inner();
                });
            }
        }

        // ================= 手牌引擎 =================

        // 刃 / 灯：手牌 = ③「丢弃自身 → 抽 1」，场上 = ②「弹对手 1 张卡」。两种都开。
        // 为什么手牌一定要丢：Lv7 要 2 个祭品才能通召，靠抽卡自跳是唯一上场方式；
        // 留在手里既上不了场也当不了素材，只剩「丢手抽 1」这一个用途。
        private bool DiscardOrBounce()
        {
            // ★ v4.4：水母 hold 模式 → 手牌里的刃/灯一律不发动（① 自跳 / ③ 丢弃抽卡都从手牌动了怪，喂水母抽 2）。
            //   留着等到对方回合再用。已经在场上的 ② 弹卡不受这条限制（那是场上的怪在动，不触发水母）。
            if (_jellyfishFreeze && Card.Location == CardLocation.Hand)
            {
                Log("刃/灯 水母停铺 → 手牌不发动（留着）" + JellyfishFreezeWhy());
                return false;
            }
            // ★ v5.3：场上的刃/灯被无效了 → ② 弹卡照样会被无效，别浪费。
            if (EffectNegated()) return false;

            bool isHiena = Card.IsCode(CardId.Hiena);

            // 诊断：后攻首回合第一次在手牌遇到刃/灯，把 desc 打一次。
            // ① 抽到自跳 与 ③ 丢弃抽 1 都在手牌，全靠 desc 区分；万一引擎没按 SetDescription 下发，
            // 这条日志能直接看出实际值，改判据就有依据（每场只打一次，不刷屏）。
            if (!_descProbeLogged && Duel.Turn == 1 && Duel.Player == 1
                && Card.Location == CardLocation.Hand)
            {
                _descProbeLogged = true;
                Log("刃/灯 desc 观察：" + CardName() + " desc=" + ActivateDescription
                    + "（① 预期 " + ((isHiena ? CardId.Hiena : CardId.Kurusu) << 4) + "）");
            }

            // ★ v5.5 ①：抽到自跳 —— 后攻首回合要分档（见 ShouldSelfSummonOnDraw）。
            //   desc 不匹配时（引擎没给 desc）这里不会命中，退化成原来「一律跳」，行为不会更差。
            if (Card.Location == CardLocation.Hand
                && ActivateDescription == (isHiena ? DescHienaSummonOnDraw : DescKurusuSummonOnDraw)
                && !ShouldSelfSummonOnDraw(Card))
            {
                Log((isHiena ? "希耶娜" : "库露雪") + " ① 抽到自跳 → 对方"
                    + (isHiena ? "怪兽区是空的（② 弹不到怪兽）" : "魔陷区是空的（② 弹不到魔陷）")
                    + " → 留手牌");
                return false;
            }

            // ★ v5.5 ②：弹卡先挑目标 —— 希耶娜弹对方打点最高 / 最有压制力的怪兽，
            //   库露雪弹对方最有价值的魔陷。见 PickBounceMonsterTarget。
            if (Card.Location == CardLocation.MonsterZone)
            {
                _bounceTarget = isHiena ? PickBounceMonsterTarget() : PickBounceSpellTarget();
                if (_bounceTarget != null)
                {
                    Log((isHiena ? "希耶娜" : "库露雪") + " ② 弹 -> " + _bounceTarget.Name
                        + "（" + (isHiena
                            ? "对方场上打点最高的一只，" + _bounceTarget.Attack + " ATK"
                            : "对方场上最值钱的一张") + "）");
                }
            }

            Log("刃/灯 发动 " + CardName() + " @" + Location(Card));
            return true;
        }

        // ★ v5.5 用户口径：只在「升月后攻、对方先攻的那一个回合」分档 —— 即决斗第 1 回合的对方回合
        //   （Duel.Turn == 1 && Duel.Player == 1）。升月是先攻时第 1 回合是我方，不进这个分支。
        //   其它回合一律照常自跳（跳下来就能立刻接到超量，没必要留）。
        //   为什么这回合要分：
        //     对方检索 → 朔的「每次对方检索我方抽 1」会连着抽，这一波可能一次抽到好几只本家。
        //     希耶娜 / 库露雪的 ① 跳下来只有 ② 一个用处（弹对方 1 张卡），
        //     对方场上没东西可弹时跳下来 = 白占一个怪兽区、还把「被抽到时自跳」这个一次性的机会用掉；
        //     留手里反而能在对方真正铺开（或下回合自己回合）时再跳。
        //   菈碧 / 卡萝尔不在此列：
        //     · 菈碧跳下来占位，后面再抽到本家还能继续跳，是凑 R7 的料（守备落地见 OnSelectPosition）；
        //     · 卡萝尔跳下来立刻 ③ 抽 1，本身就是赚的。
        private bool ShouldSelfSummonOnDraw(ClientCard self)
        {
            if (Duel.Turn != 1 || Duel.Player != 1) return true;
            if (self.IsCode(CardId.Hiena)) return Enemy.GetMonsters().Count > 0;   // 弹得到怪兽才跳
            if (self.IsCode(CardId.Kurusu)) return Enemy.GetSpells().Count > 0;    // 弹得到魔陷才跳
            return true;
        }

        // 希耶娜 ② 弹对方怪兽：优先「攻击力最高、有压制力的」那只。
        //   ★ 用户口径（replay/优化8.yrp）：默认选卡挑到了低价值怪，实际上该弹对方场面里最硬的那一点 ——
        //     弹回卡组最下面是彻底洗掉，解的必须是压制力最强的（融合 / 同步 / 超量 / 连接这类终端）。
        //   评分：表侧 +10000（里侧看不到攻守，价值判断不成立）、额外卡组怪 +2000、然后比攻击力。
        private ClientCard PickBounceMonsterTarget()
        {
            return Enemy.GetMonsters()
                .Where(c => c != null && c.HasType(CardType.Monster))
                .OrderByDescending(c => (c.IsFaceup() ? 10000 : 0) + (c.IsExtraCard() ? 2000 : 0) + c.Attack)
                .FirstOrDefault();
        }

        // 库露雪 ② 弹对方魔陷：表侧优先（永续 / 场地 / 装备是正在生效的），场地再优先一点；
        // 里侧盖卡价值未知，排最后。
        private ClientCard PickBounceSpellTarget()
        {
            return Enemy.GetSpells()
                .Where(c => c != null)
                .OrderByDescending(c => (c.IsFaceup() ? 10000 : 0)
                    + (c.Location == CardLocation.FieldZone ? 500 : 0))
                .FirstOrDefault();
        }

        // 帐·西艾罗（75934716）
        //   ① 被抽到时 → 给对方看 → 自跳
        //   ② 手牌：返 1 张卡回卡组最下面 → 自跳（Lv7 要 2 祭品，留在手里就是废牌）
        //   ③ 场上：自己·对方的主要阶段，自己抽 1
        //      （代价：本回合不能用抽卡以外的方法从卡组把卡加入手卡）
        private bool SieroActivate()
        {
            if (Card == null) return false;
            // ★ v5.3：被无效的帐，①/②/③ 全部只会白费。
            if (EffectNegated()) return false;

            // ★ v4.4：水母停铺 → 手牌里的帐不发动（① 抽到自跳 / ② 返卡自跳都从手牌动了怪）。
            //   已经在场上的 ③ 抽 1 不受影响。
            // ★ v5.42 用户口径：「如果手牌有辉煌还是尽量不要从手牌召唤特殊召唤怪兽吧
            //   （**西艾萝除外**，她特殊召唤到场上可以抽 1，对方回合还能抽 1）」
            //   ⇒ 只有 `_holdMode`（手牌有辉煌那条线）放行她；`_jellyfishBoardSet`（大姐已在场 /
            //     本家已有 2 只）那种「场面已经够用」的情况仍然停手 —— 那时候再跳只是白送抽。
            if (_jellyfishFreeze && !_holdMode && Card.Location == CardLocation.Hand)
            {
                Log("帐 水母停铺 → 手牌不发动（留着）" + JellyfishFreezeWhy());
                return false;
            }

            // ---- ① / ②：在手牌就是该跳，别留着 ----
            if (Card.Location == CardLocation.Hand)
            {
                if (!HasFreeMonsterZone())
                {
                    Log("帐 手牌 · 无空怪兽区，留守");
                    return false;
                }
                if (ActivateDescription == DescSieroHandSS)
                {
                    _returnHandPending = true;   // 只有 ② 要返 1 张卡，① 不用
                    _returnHandIsCity = false;   // ★ v5.22：帐② 卡文写死「回到卡组最下面」，没有顶/底可选
                    Log("帐 ② 返1卡自跳");
                }
                else
                {
                    Log("帐 ① 抽到自跳");
                }
                return true;
            }

            if (Card.Location != CardLocation.MonsterZone)
            {
                Log("帐 发动 @" + Location(Card));
                return true;
            }

            // ---- ③ 抽 1 ----

            // ★ v5.14 例外（比「另开连锁」更重要，必须放在 InChain 之前）：这张卡自己马上就要离场了。
            //   两种来源都算：
            //     · 被对方的效果取为对象（旋风 / 雷破 / 强脱）→ Util.IsChainTarget；
            //     · 对方发动了不取对象的群体清场（黑洞 / 雷击 / 大岚 / 天霆号）→ ChainHasZoneWipe。
            //   ③ 的脚本是 SetRange(LOCATION_MZONE) —— 一旦它离开怪兽区，这个效果连「发动权」都没有，
            //   根本没有下一个自由时点可等。所以这里是最后的机会：不管在不在连锁里，直接抢开，
            //   把抽 1 结算在它消失之前。
            if (SelfDoomedOnField(RaiseMoonScriptWipe.Monster))
            {
                Log("帐 ③ 自身将被破坏 → 连锁抢开抽1");
                return true;
            }

            // ★ v5.14 用户口径（replay _LastReplay，16:11~16:15）：**抽卡效果一律另开连锁** ——
            //   自己回合也一样。v5.13 之前这条 InChain 闸门只放在「自己回合」分支之后，
            //   所以自己回合里「大姐拔1抽1 / 帐① 自跳 / 刃灯丢手抽1 / 辉煌②」之后引擎再问 ③ 时，
            //   它会 C2 跟着自己的卡一起上链（录像里 16:14:58 那一手就是这么抽的）。
            //   为什么必须另开：
            //     · 连锁处理中抽到的本家，「被抽到时自跳」接不上这一条连锁，只能等整条处理完；
            //     · 两张抽卡挤进同一条连锁 = 白白浪费掉 C1 结算后那个自由时点；
            //     · 抽卡是升月唯一的资源来源（总纲第 3 条），时点比牌本身更贵。
            //   例外只有上面那条「自身将被破坏」：等这条结算完，③ 已经没有发动权了。
            if (InChain())
            {
                Log("帐 ③ 连锁中 → 不跟，等这条结算完再另开 C1");
                return false;
            }

            // 自己回合：自由时点随时开。抽上来的本家「被抽到时自跳」是开新连锁的诱发效果，
            // 结算后能立刻跳下来接着展开。
            if (Duel.Player == 0)
            {
                // ★ v5.40：水母 combo 置顶的那张要留到对方回合 → 自己回合这一抽压住。
                if (JellyfishHoldingTop())
                {
                    Log("帐 ③ → 水母 combo 置顶的那张要留给对方回合大姐抽 1 → 这次不抽");
                    return false;
                }
                Log("帐 ③ 自己回合抽1");
                return true;
            }

            // 自由时点（可以另开 C1）：★ 等对方真的把东西摆出来再抽。
            //   判据见 EnemyDeployed()：
            //     · 对方怪兽区登场了怪兽（含里侧盖怪 —— 那是已经落到场上的布置）；
            //     · 对方魔陷区有**表侧**的卡（已发动的永续魔法 / 永续陷阱 / 装备 / 场地）。
            //   ★ 里侧盖卡不算、发完就进墓的通常魔法也不算 —— 对方主阶 1 先甩一张「强欲而金满之壶」
            //     这类滤抽，场上还是空的，这时候另开 C1 抽 1 就是白交时点：
            //     抽到的本家虽然能自跳，但抽完就没有第二个自由时点去接它做场了；
            //     对方后面真摊开来打时，我们反而已经把这个回合的名额（SetCountLimit）用掉了。
            if (EnemyDeployed())
            {
                Log("帐 ③ 对方已铺开 → C1 抽1");
                return true;
            }

            // 对方场上还空着：主阶 1 先留着等它铺（对方每次召唤 / 发动 / 盖卡后引擎还会再问）。
            //
            // ★ v5.9 用户口径（replay/优化10）：**把原来那两处「兜底」删掉**。
            //   旧版在「主阶 2」和「手牌已空」时会硬抽一张，理由是「这个回合不抽就没机会了」。
            //   但对方空场时抽卡是纯亏：「抽到希耶娜 / 库露雪换不到对方场上的卡 —— 血亏」，
            //   而对方场上只有一张开完就进墓的通常魔法时，也正是旧版最容易硬抽的时点。
            //   正确时机：等他真的把怪摆上场、或者在魔陷区贴出表侧永续 / 场地 / 灵摆之后再抽，
            //   抽到的本家落地就能弹。这一次抽卡是「每回合 1 次」的名额，宁可不发也不空放。
            Log("帐 ③ 对方还没铺开 → 留着（等它登场怪兽 / 发动永续魔陷/场地）");
            return false;
        }

        private bool CarolActivate()
        {
            // ★ v5.3：被无效的雅，①/②/③ 都白费。
            if (EffectNegated()) return false;
            // ★ v4.4：水母 hold 模式 → 手牌里的雅不发动（① 抽到自跳从手牌特招，喂水母抽 2）。
            //   已经在场上的 ②（不解放通召）不受影响（但 hold 模式里雅本来就上不了场）。
            if (_jellyfishFreeze && Card.Location == CardLocation.Hand)
            {
                Log("雅 水母停铺 → 手牌不发动（留着）" + JellyfishFreezeWhy());
                return false;
            }
            Log("雅 发动 @" + Location(Card));
            _lightLock = true;   // ③ 通过后本回合只能从额外特招光属性超量（大姐 / 千金大奖 / 光咆哮都是 LIGHT）
            return true;
        }

        // 翼·菈碧
        //   手牌：①「被抽到时 → 给对方看 → 特殊召唤」——自跳才能开 ③，且还能再抽 1
        //   场上·自己回合：只有 ②「抽 1」
        //   场上·对方回合：③「进行 1 只光属性 7 阶超量召唤」——
        //     ★ 不要一进对方回合就开，等对方真的把怪摆上来或正在发动卡（连锁上有对手的卡）再开，
        //       否则千金大奖的拔素材弹卡会弹不到东西。
        private bool RabiActivate()
        {
            if (Card == null) return false;
            // ★ v5.3：被无效的菈碧，①②③ 全部白费。
            if (EffectNegated()) return false;
            // ★ v4.4：水母 hold 模式 → 手牌里的菈碧不发动（① 抽到自跳从手牌特招，喂水母抽 2）。
            //   已经在场上的 ② 抽 1 / ③ 加速不受这条限制。
            if (_jellyfishFreeze && Card.Location == CardLocation.Hand)
            {
                Log("翼 水母停铺 → 手牌不发动（留着）" + JellyfishFreezeWhy());
                return false;
            }
            if (Card.Location == CardLocation.Hand)
            {
                Log("翼 ①抽到自跳");
                return true;
            }
            if (Duel.Player == 0)
            {
                Log("翼 ②抽1");
                _lightLock = true;
                return true;
            }
            // ★ v5.5：对方回合也要能发 ②「从手卡召唤·特殊召唤的场合，自己抽 1」。
            //   这一抽正是「继续抽到本家、继续跳下来凑 R7」的链条，
            //   以前这里直接落到 ③ 加速的判断，③ 不 ready 就 return false，把 ② 的抽 1 一起吞掉了。
            // ★ v5.9 用户口径（replay/优化10）：抽到菈碧的正确节奏是「**先抽、再加速**」——
            //   ① 抽到即自跳 → ② 特招成功抽 1 → 之后另开连锁 ③ 加速超量。
            //   ②/③ 在引擎里走的是**两种不同的询问**，可以精确分流：
            //     · ② 是诱发效果「可以发动吗」→ 走 MSG_SELECT_EFFECTYN，此时 `AI.SelectingEffectYn == true`；
            //     · ③ 是 QUICK_O，只出现在连锁 / idle 的可发动列表里，SelectingEffectYn 恒为 false。
            //   v5.5 那套「③ 优先、靠 _rabiArrivedTurn 认 ②」会把 ② 的抽 1 直接吞掉（③ 先吃掉这一手），
            //   现在改成 SelectingEffectYn 分流，两个效果都能吃到。
            if (AI.SelectingEffectYn)
            {
                Log("翼 ②抽1（对方回合 · 特招成功诱发）");
                return true;
            }
            if (RabiXyzReady())
            {
                Log("翼 ③加速R7 -> 发动");
                return true;
            }
            Log("翼 ③加速R7 -> 按兵不动");
            return false;
        }

        // 对方回合能不能开 ③「进行 1 只光属性 7 阶超量召唤」。
        // ★ v5.9 用户口径（replay/优化10）三条触发场景：
        //   ① 我方场上没有大姐、对方在发动「检索」→ 加速**大姐**：大姐 e5（EVENT_CHAIN_SOLVING，
        //      CATEGORY_SEARCH/DRAW，且 GetSequence()>4 = EX 区）会在对方检索**结算前**把它换掉，
        //      对手这次检索变成随机抽 1 —— 等于一次免费打断（具体选谁见 PreferredAccelXyz）。
        //   ② 我方已有大姐、或对方不是检索（要从手牌特招怪兽）→ 加速**千金大奖**：它 ② 的 e3 触发条件
        //      正是「对方从手牌把怪兽召唤/特招成功」（s.tdcon2 = IsSummonLocation(LOCATION_HAND)），
        //      卡在这一环上，他的怪一落地我们就能拔 2 弹 2。
        //   ③ 对方场上已经摆着 ≥2 张可弹的卡 → 千金大奖落地直接拔 2 弹 2（用户：「此时千金大奖也能弹到对方卡」）。
        //   其余情况（对方什么都没做 / 只丢手坑）→ 不动，素材和 ③ 的名额都留着。
        private bool RabiXyzReady()
        {
            if (Duel.Player != 1) return false;
            if (Bot.GetMonstersInMainZone().Count(c => c.Level == 7) < 2) return false;   // 场上凑不出 2 只 Lv7

            // ③ 只能用「光属性 7 阶、而且当场叠得出来」的超量（c47828069 的 xyzfilter → IsXyzSummonable）。
            // 千金大奖通常还有 3 张；大姐额外要求空 EX 区 + 场上没有第二张。
            bool canBounty = Bot.ExtraDeck.Any(c => c.IsCode(CardId.Bounty));
            bool canJackpot = Bot.ExtraDeck.Any(c => c.IsCode(CardId.Jackpot))
                && FreeEmzCount() > 0 && !HasSpellcasterXyz();
            if (!canBounty && !canJackpot) return false;

            ClientCard last = Util.GetLastChainCard();
            // (a) 对方在场上的卡发动效果（怪兽 / 永续 / 场地 / 魔陷）
            bool enemyChaining = last != null && last.Controller == 1 && last.Location != CardLocation.Hand;
            // (b) 对方从**手牌**发动怪兽效果 —— 场景 ②（从手牌特招）。
            //     手坑不算：它们不特招，加速上去等于空放 —— 用 HandTrapIds 剔掉（v5.13 起陨石也在里面，
            //     原来那句 `last.Id != CardId.Nibiru` 就不用单写了）。
            bool enemyHandSummon = last != null && last.Controller == 1 && last.Location == CardLocation.Hand
                && last.HasType(CardType.Monster) && !HandTrapIds.Contains(last.Id);
            // (c) 对方场上已经有可弹的卡（≥2 张）—— 场景 ③
            bool enemyBoard = Enemy.GetMonsterCount() + Enemy.GetSpellCount() >= 2;
            return enemyChaining || enemyHandSummon || enemyBoard;
        }

        // 对方回合「翼 ③ 加速」要叠哪一只（真正落地在 OnSelectCard 的「从额外挑超量」那一段）。
        private int PreferredAccelXyz()
        {
            // 自己回合的加速 / 辉煌① 拉怪：EX 区封锁 + 魔法师族，一律大姐
            if (Duel.Player != 1) return CardId.Jackpot;

            ClientCard last = Util.GetLastChainCard();
            bool enemySearch = last != null && last.Controller == 1 && RaiseMoonScriptWipe.TakesFromDeckToHand(last.Id);
            bool exFree = FreeEmzCount() > 0;

            // 场景 ①：我方场上没有大姐 + 对方这一环是检索 + 自己的 EX 区是空的 → 大姐
            //   ★ EX 区不空时大姐 e5 的 `GetSequence()>4` 不成立，替换检索根本不生效，叠了就是白亏 2 个素材，
            //     所以 `exFree` 是这个分支的硬条件。
            if (enemySearch && exFree && !HasSpellcasterXyz()
                && Bot.ExtraDeck.Any(c => c.IsCode(CardId.Jackpot)))
            {
                Log("翼③ 加速 -> 大姐（对方这次是检索 → 换成对方随机抽 1）");
                return CardId.Jackpot;
            }

            // 场景 ②③：其余一律千金大奖 —— 拔 2 弹 2 是实打实的资源交换
            // ★ v5.15 用户口径（优化16-2）：加速目标要「看情况灵活挑」，不是死板一张千金大奖：
            //   (a) 对方场上有 3 只以上怪兽、我方也有 3 只 7 星 → **托勒密Ω7**：拔 3 弹 3 回卡组，
            //       回卡组连遗言 / 墓地回收都不给，比千金大奖弹 2 干净（素材多一只，正好对应多弹一张）；
            //   (b) 对方在吃墓地（已露墓地利用 + 墓地非手坑怪 ≥3）→ **No.76**：把墓地的关键怪吸成素材，
            //       断掉它的墓地起跳 / 回收，同时给自己加 1 个素材；
            //   (c) 其余（含已经有千金大奖在场、要再弹一次）→ 千金大奖。
            //   千金大奖的 ② 是一回合一次按「卡名」算的吗？—— 不是「这张卡」的限制，
            //   第二张落地就能再拔 2 弹 2，所以「已有千金大奖」时继续选它是对的。
            int bodies = Bot.GetMonstersInMainZone().Count(c => c.Level == 7);
            if (bodies >= 3 && Enemy.GetMonsterCount() >= 3
                && Bot.ExtraDeck.Any(c => c.IsCode(CardId.Omega7)))
            {
                Log("翼③ 加速 -> 托勒密Ω7（对方 3 只怪 → 拔 3 弹 3 回卡组）");
                return CardId.Omega7;
            }
            if (bodies >= 2 && Bot.ExtraDeck.Any(c => c.IsCode(CardId.Utopia76)) && GraveWorthUtopia76())
            {
                Log("翼③ 加速 -> No.76（对方墓地 " + EnemyGraveMonsterCount() + " 只非手坑怪 → 挖墓）");
                return CardId.Utopia76;
            }

            Log("翼③ 加速 -> 千金大奖（拔 2 弹 2）");
            return CardId.Bounty;
        }

        // 「对方墓地里的非手坑怪兽数」—— 手坑进墓不是「吃墓地」的信号（与 XyzUtopia76 同一口径）。
        private int EnemyGraveMonsterCount()
        {
            return Enemy.GetGraveyardMonsters().Count(c => c != null && !HandTrapIds.Contains(c.Id));
        }

        // No.76 值不值得为它花一次超量（对方回合的加速版，口径同 XyzUtopia76 的第 ②③ 条）。
        private bool GraveWorthUtopia76()
        {
            int n = EnemyGraveMonsterCount();
            return n >= 3 && (_enemyGravePlay || n >= 4);
        }

        // 朔·斯奎兹：手牌丢弃自身，二选一（两个效果 1 回合各能用 1 次）
        //   ①● 这个回合每次「抽卡以外的方法」让对方手卡有卡加入 → 我方抽 1
        //       （持续，代价：直到下个回合结束只能从额外卡组特招 7 阶超量）
        //   ①● 自己抽 1 张。
        // ★ v5.19 用户口径（replay _LastReplay 22:25）—— 两个效果**策略不同**，不能一起改：
        //   「抽1效果要另开连锁，而另一个对应检索的持续抽卡效果要跟连锁。」
        //   · 「自己抽 1」是收益即刻到手的效果 → 只在**自由时点**当 C1 发。
        //     ★ v5.20 理由纠正（用户口径）：另开连锁的目的是**让后续抽到的本家怪兽能跳下来** ——
        //     「被抽到时可以特殊召唤」是抽卡那一刻的诱发，连锁中抽卡时那个时点已经过去。
        //     （「不堵手坑时点」是顺带的好处，不是原因。）
        //   · 「持续抽卡」是给这个回合后续动作装一个反制 → **必须 C2 跟对方的检索**。
        //     它要赶在检索结算之前挂上，否则对方这一次检索已经结算完，这一抽永远补不回来。
        //   · ★ v5.20 协同：手卡同时有**灰流丽**时，对方的**第一个检索先交给灰流丽无效**，
        //     朔不跟（`AshWillTakeChain`）；灰流丽 1 回合 1 次用掉之后，对方再开检索才轮到朔。
        //   所以：「抽 1」永远等自由时点，「持续抽卡」永远跟链（除非让给灰流丽）。
        //
        // ★ v5.36 用户口径（replay _LastReplay 02:08 局）—— 在上面两条之上再加两道闸门：
        //   1) 对方检索 + 场上有大姐时**分情况**（朔是二选一，同一张牌同一回合名额）：
        //        手牌 ≤ `SquizThinHand`（2 张，含朔）→ 朔挂持续抽卡（断粮时「每检索一次抽一张」更值）；
        //        手牌够 → 不跟链，让大姐把检索换成对方随机抽 1，朔留着。
        //      大姐那侧同步让位（`_squizContinuous`）：朔装上了就别再替换，否则抽 1 永不触发。
        //   2) 对方回合要不要用「抽 1」：只看**对方场上有没有怪兽 / 魔陷** ——
        //        有 → 另开 C1 抽 1（抽到本家正好下去解场）；没有 → 留着。
        private bool SquizActivate()
        {
            if (Card == null || Card.Location != CardLocation.Hand) return false;

            if (Duel.Player == 0)
            {
                // 自己回合：只发「自己抽 1」，且一定另开连锁（不 C2 跟自己的卡）。
                // ★ v5.20 纠正理由（用户口径）：抽 1 另开连锁**不是为了挡手坑**，而是因为
                //   「被抽到时可以特殊召唤」是**抽卡那一刻**的诱发 —— 这次抽卡如果发生在连锁里（C2+），
                //   那个诱发时点已经过去，抽到的雅 / 帐 / 菈碧 / 希耶娜 / 库露雪 全部烂在手里。
                //   另开一条 C1 再抽，它们才有各自的自跳窗口、才能接着把场面续上。
                if (InChain())
                {
                    Log("朔 抽1 不跟链（连锁中抽到的本家跳不下来）→ 等这条结算完再 C1 丢");
                    return false;
                }
                // ★ v5.40：水母 combo 置顶的那张要留到对方回合 → 这次丢弃抽 1 也压住。
                if (JellyfishHoldingTop())
                {
                    Log("朔 抽1 → 水母 combo 置顶的那张要留给对方回合大姐抽 1 → 这次不丢");
                    return false;
                }
                AI.SelectOption(1);
                Log("朔 自己回合 → 自己抽1");
                return true;
            }

            // ---- 对方回合 ----
            ClientCard last = Util.GetLastChainCard();
            if (last != null)
            {
                // 连锁里只做一件事：对方检索 → 跟上「持续抽卡」。
                // 判据：链上最后一环是对方的卡、不是从手牌发动（手坑），且 lua 会「从卡组把卡加入手卡」。
                // 对方从卡组**堆墓** / 从墓地回收 / 单纯抽卡都不满足这条 —— 那时候丢掉朔什么都换不回来。
                if (last.Controller == 1 && last.Location != CardLocation.Hand
                    && RaiseMoonScriptWipe.TakesFromDeckToHand(last.Id))
                {
                    // ★ v5.20 用户口径：手卡同时有灰流丽和朔时 ——
                    //   **对方的第一个检索先交给灰流丽无效**；对方再开第二个检索时，
                    //   灰流丽 1 回合 1 次已经用掉，这时才轮到朔挂「持续抽卡」。
                    //   问的是「灰流丽这一环会不会真的交」（`AshWillTakeChain`），
                    //   而不是「手里有没有灰流丽」—— 大姐在场那种「主动不交」的情况要让朔照常跟链，
                    //   否则两头都空着，对方这一次检索白送。
                    if (AshWillTakeChain())
                    {
                        Log("朔 让给灰流丽 → 对方第一个检索先无效，朔等它再开第二个检索再挂持续抽卡");
                        return false;
                    }

                    // ★ v5.36 用户口径（replay _LastReplay 02:08 局）：
                    //   「手卡有朔、场上有大姐，对方发动检索效果时，分情况 —— 如果升月手牌很少，
                    //     那可以发动朔的持续抽卡效果；其他情况则是大姐置换对方的检索效果。」
                    //   为什么要分：朔是**二选一**（●1 自己抽 1 / ●2 本回合每次对方从卡组加手卡都抽 1），
                    //   两个选项共用同一张牌、同一回合名额。
                    //     · 手牌少 → 选 ●2：断粮时「对方每检索一次就抽一张」比一次性抽 1 强得多；
                    //     · 手牌够 → 让给大姐：精准检索被换成对方随机抽 1，直接掐掉他这条展开线，
                    //       朔留在手里，之后对方场上有东西时再另开 C1 抽 1 去挖本家解场。
                    //   （02:08 那一局是两头都发了：朔挂了持续抽卡，大姐还把检索换掉了 ——
                    //     结果朔那次抽 1 根本不会触发，白扔一张牌。）
                    int myHand = MyHandCount();
                    bool jackpotCanReplace = JackpotWillReplaceSearch(last);
                    if (myHand > SquizThinHand && jackpotCanReplace)
                    {
                        Log("朔 手牌 " + myHand + " 张（> " + SquizThinHand + "）不算少 → 让大姐置换这条检索"
                            + "（朔留着，之后对方场上有东西再另开 C1 抽1）");
                        return false;
                    }

                    AI.SelectOption(0);
                    _squizContinuous = true;   // ★ v5.36：本回合朔已挂持续抽卡 → 大姐在同回合的检索上让位
                    Log("朔 对方检索（手牌 " + myHand + " 张"
                        + (jackpotCanReplace ? " ≤ " + SquizThinHand + " 算很少" : "，场上没有大姐可置换")
                        + "）→ C2 跟链挂持续抽卡");
                    return true;
                }
                // 其余连锁（对方不是检索 / 链上是我方的卡）→ 抽 1 留到自由时点，不污染连锁。
                Log("朔 连锁中 → 抽1 不跟（持续抽卡只跟对方检索）");
                return false;
            }

            // ---- 自由时点：抽 1 ----
            // ★ v5.36 用户口径：「朔这回合发不发动抽1效果，则看对方场上有没有怪兽或者魔法陷阱卡，
            //   有的话可以另开连锁发动抽1效果（这样做目的是如果抽到本家可以解对方场）。」
            //   ⇒ 判据只看**对方场面有没有东西**：空场时抽到本家也无处可解，这手留着更值
            //     （下一步对方铺开时再抽，落点才对得上）。v5.19 那条「对方无动作 → 自己抽1」据此撤销。
            //   时点限制：抽卡 / 准备阶段的自由时点不抢 —— 那时对手还没铺开，先抽等于提前花掉这张牌；
            //   主阶 1 之后（含战阶 / 主阶 2 / 结束阶段）才动。
            bool mainOrLater = Duel.Phase == DuelPhase.Main1 || Duel.Phase == DuelPhase.BattleStart
                || Duel.Phase == DuelPhase.Battle || Duel.Phase == DuelPhase.Main2
                || Duel.Phase == DuelPhase.End;
            if (mainOrLater && EnemyBoardHasCards())
            {
                AI.SelectOption(1);
                Log("朔 对方场上 " + Enemy.GetMonsters().Count + " 怪 / "
                    + Enemy.GetSpells().Count
                    + " 魔陷 → 另开 C1 抽1（抽到本家就下去解它的场）");
                return true;
            }

            Log("朔 对方空场（或还没进主阶）→ 手里留着，不抽");
            return false;
        }

        // 对方场上有没有「值得解的东西」：怪兽 / 魔陷区（`GetSpells()` 取的是整个 SpellZone，
        // 魔法、陷阱、表侧永续、里侧盖卡都算在内 —— ClientField 没有单独的 GetTraps）。
        private bool EnemyBoardHasCards()
        {
            return Enemy.GetMonsters().Count > 0 || Enemy.GetSpells().Count > 0;
        }

        // 我方手牌张数（去掉 WindBot 手里的 null 空位）。
        private int MyHandCount()
        {
            return Bot.Hand.Count(c => c != null);
        }

        // ================= 魔陷 =================

        // 群星②：把墓地的这张卡除外 → 抽 1。排在 ① 前面，保证「置顶一张、立刻抽回来」成对发生。
        private bool StarsBanish()
        {
            if (Card == null || Card.Location != CardLocation.Grave) return false;
            // ★ v4.4：水母 hold 模式 → 墓地群星也不抽（抽到的本家在 hold 里自跳不了，白弃）。
            if (_holdMode) return false;
            // ★ v5.40：combo 已经置顶了一张本家 → 现在抽走等于把这张从牌堆顶挪回手里，
            //   对方回合大姐抽 1 就抽不到它了（而本回合它又跳不了）→ 这次不抽。
            if (JellyfishHoldingTop())
            {
                Log("群星 ② → 水母 combo 置顶的那张要留给对方回合大姐抽 1 → 这次不抽");
                return false;
            }
            // ★ v5.2 顺序：手里还有副葬 / 群星① 还没置顶时先别抽 —— 见 HoldDrawForOrder。
            if (HoldDrawForOrder()) return false;
            AI.SelectOption(HasSpellcasterXyz() ? 1 : 0);   // 1 = 只自己抽（要魔法师族本家超量）；0 = 双方各抽 1
            Log("群星 ②墓地除外抽1");
            return true;
        }

        // 群星①：从卡组把 1 张本家置顶。
        // ★ 绝对不能查「卡组里有没有」——AI 看不到自己的卡组（Deck 里未抽到的卡 Id 全是 0），
        //   以前用 Bot.Deck.Any(...) 判断导致这张卡永远不发动。直接开，挑哪张交给 OnSelectCard。
        private bool StarsTop()
        {
            if (Card == null || Card.Location == CardLocation.Grave) return false;

            // ★ v5.2 顺序：手里同时有副葬时，先让副葬发。
            //   副葬会让卡组重洗 —— 先置顶再副葬等于把置顶洗掉。副葬发完（洗牌落定）再置顶才有效。
            //   副葬这回合发不了（辉煌② 名额已用 / 水母 hold）时不算，否则群星会一直让位发不出去。
            if (CanUseBurialNow())
            {
                Log("群星 ① 手里有副葬 → 让副葬先发（先洗牌堆辉煌，再置顶）");
                return false;
            }

            _starsToppedThisTurn = true;   // 置顶已发 → 解除「抽卡延后」
            _stackTarget = -1;
            Log("群星 ①置顶（目标待挑）");
            return true;
        }

        // 「副葬现在能不能发」—— 和 BurialActivate 的前置保持一致，供顺序判断复用。
        private bool CanUseBurialNow()
        {
            // ★ v5.42：v4.4 的 `if (_holdMode) return false;` 撤销 —— hold 期间副葬照样要发（只堆不抽），
            //   而且它必须排在群星① 置顶**之前**（副葬会让卡组重洗，先置顶会被洗走）。
            if (_splendorGraveUsed) return false;    // 本回合辉煌② 名额已用，堆下去也抽不了
            return Bot.Hand.Any(c => c != null && c.IsCode(CardId.Burial));
        }

        // ★ v5.2：副葬 + 群星同时在手时的「抽卡延后」闸门。
        //   用户口径：先发副葬堆辉煌（**不抽**）→ 再发群星置顶 → 之后才允许辉煌② / 群星② 抽卡。
        //   为什么：副葬会让卡组重洗。若先置顶再抽、抽完再副葬，洗牌会把置顶打乱；
        //   而如果置顶后抽卡被打断（灰流丽之类），想要的卡没到手、又被副葬洗走，就彻底拿不到了。
        //   ⇒ 顺序固定成「洗牌 → 置顶 → 抽」，抽卡类效果在置顶完成前一概让路。
        private bool HoldDrawForOrder()
        {
            if (Duel.Player != 0) return false;      // 只在自己回合讲这个顺序
            if (_starsToppedThisTurn) return false;  // ① 已经置顶过 → 放行
            if (!Bot.Hand.Any(c => c != null && c.IsCode(CardId.Stars))) return false;  // 手里没群星 → 放行

            bool waitBurial = CanUseBurialNow();     // 副葬还没发
            bool waitTop = _burialUsedThisTurn;      // 副葬已发，但群星还没置顶
            if (!waitBurial && !waitTop) return false;

            if (!_orderHoldLogged)
            {
                _orderHoldLogged = true;
                Log(waitBurial
                    ? "抽卡 延后 → 先让副葬堆辉煌（手牌有副葬+群星）"
                    : "抽卡 延后 → 先让群星置顶（副葬已堆辉煌），置顶后再抽");
            }
            return true;
        }

        private bool CityActivate()
        {
            // ★ v5.23 用户纠正：未眠之城的『盈彩月夜』是**永续魔法**（type 0x20002 = SPELL|CONTINUOUS），
            //   落在魔陷区（`CardLocation.SpellZone`），不是场地魔法区（`FieldZone` = 256）。
            //   v5.22 一直写着 `FieldZone` → onField 恒为 false → `_returnHandPending` /
            //   `_returnHandIsCity` 从来没被置过，`PickCityReturnCard` 一次都没跑到（返卡其实一直走的是
            //   OnSelectCard 的兜底 `HandCardValue`）。这里按「魔陷区或场地魔法区」判，两种都算在场上。
            bool onField = Card != null
                && (Card.Location == CardLocation.SpellZone || Card.Location == CardLocation.FieldZone);

            // ★ v5.37 用户口径：「当升月中了小丑与锁鸟效果适用的时候，未眠之城还是留在手牌不要发动吧。」
            //   依据 c94145021.lua 的 operation：锁鸟一到场不只封「从卡组把卡加入手卡」，还挂了
            //   `EFFECT_CANNOT_DRAW`（双方，到回合结束）；而 ② 的两个选项在 s.drtg 里都要先过
            //   `Duel.IsPlayerCanDraw(tp,1)`（b1 / b2 各自带这一条）⇒ 这一回合这张牌**发不出任何效果**。
            //   ⇒ 手上那张不铺（铺下去等于把一张能留到下回合的手牌提前交出去，还白占一格魔陷区），
            //     场上那张也不发 ② —— 整张牌留到封锁解除（下个回合）再说。
            //   代价：这一回合的 ③（结束阶段把墓地本家洗回卡组）也跟着没了，按用户口径接受。
            if (DrollAppliedThisTurn())
            {
                if (!_cityDrollLogged)
                {
                    _cityDrollLogged = true;
                    Log(onField
                        ? "未眠 ② → 中了小丑与锁鸟（本回合双方都不能抽卡），●1/●2 都以抽卡为前提 → 留着不发"
                        : "未眠 → 中了小丑与锁鸟（本回合双方都不能抽卡）→ 留在手牌，等封锁解除再铺");
                }
                return false;
            }

            // ★ v5.43 用户口径：「有副葬和未眠之城一起起手的话，那未眠之城不发动，副葬发动堆墓辉煌就行。」
            //   卡文依据（`c72978038.lua` 的 e4 / `s.tdop`）：未眠 ③ 在**自己·对方的结束阶段**把
            //   墓地·除外状态的「盈彩月夜」卡**全部洗回卡组**（`SEQ_DECKSHUFFLE`）。副葬堆下去的辉煌
            //   正是 0x1ec 本家 —— 同一回合的结束阶段就被洗走，副葬那一步
            //   （1 张换 1 个「辉煌② 墓地除外抽 1」的启动点）当场作废。
            //   ⇒ 两者同时在手：这一回合只发副葬，未眠整张留手牌，下回合再铺。
            //   水母 hold 里尤其要紧：hold 下副葬只堆不抽，辉煌一定还躺在墓地，③ 一洗就什么都不剩。
            if (CityDeferToBurial())
            {
                if (!_cityBurialLogged)
                {
                    _cityBurialLogged = true;
                    Log(onField
                        ? "未眠 ② → 本回合副葬堆了辉煌，这张先不发（③ 结束阶段会把墓地·除外的本家全洗回卡组）"
                        : "未眠 → 手里同时有副葬：这张先不发（未眠 ③ 结束阶段会把刚堆进墓地的辉煌洗回卡组，副葬就白堆了）→ 只让副葬堆墓");
                }
                return false;
            }

            // A. 手上的这张 → 直接铺到魔陷区。
            //    铺卡本身没代价（② 的时机另说），而这一步**不会**有 ●1/●2 的选项询问 ——
            //    所以不能在这里 `AI.SelectOption`，否则会把值残留给下一次无关的选项（v5.22 的隐患）。
            if (!onField)
            {
                Log("未眠 → 从手牌铺到魔陷区（② 等做出大姐再发 ●2 白抽）");
                return true;
            }

            // B. 场上那张 → 发动 ②。两个分支：
            //   ●2「自己场上有魔法师族『盈彩月夜』超量怪兽存在的场合才能发动。自己抽 1 张。」→ 白拿一张
            //   ●1「选自己 1 张手卡回到卡组最上面或最下面。那之后，自己抽 1 张。」→ 净手牌不变
            if (HasSpellcasterXyz())
            {
                // ★ v5.42：水母 hold 且群星已经把一张本家压在牌堆顶 → 这次白抽会把那张抽走，
                //   「下回合大姐抽 1 抽出来自跳」的干扰就没了 → 这次不发。
                //   （hold 期间用户要的是「回手牌到卡组顶」，不是「把置顶那张抽回来」。）
                if (_holdMode && JellyfishHoldingTop())
                {
                    Log("未眠 ② 白抽 1 → 水母 hold 且群星已置顶 → 这次不抽（抽走置顶那张，下回合的干扰就没了）");
                    return false;
                }
                AI.SelectOption(1);
                Log("未眠 ② 白抽 1（场上有大姐 → ●2 无代价抽 1）");
                return true;
            }

            // ★ v5.23 用户口径：「如果升月这回合能先做出大姐，未眠的发动应以 ●2（白嫖抽 1）为优先，
            //   而不是回 1 抽 1。」——② 是永续魔法的**起动效果**，可以在主阶任意时点发，
            //   所以只要这回合还有路把大姐摆到 EX 区，就先压着不发，等它落地再发 ●2。
            //   同样是一张抽卡：●1 要先把一张手卡洗回卡组（净 0），●2 是净 +1。
            // ★ v5.42：水母 hold 期间不等大姐 —— 用户口径要的是「现在就把手里那张本家压回卡组顶」，
            //   等下去的结果是「盖完辉煌直接结束回合，② 一次都没发」。
            if (!_holdMode && CanBuildJackpotThisTurn())
            {
                if (!_cityWaitJackpotLogged)
                {
                    _cityWaitJackpotLogged = true;
                    Log("未眠 ② 暂缓 → 这回合还能做出大姐，等它落 EX 区再发 ●2 白抽 1（省下回 1 张手卡）");
                }
                return false;
            }

            // ★ v5.22 用户口径：手上只剩卡萝尔一张可洗的时候**不要发** ——
            //   她是本卡组难得的通召点（② 可以不用解放通召，③ 通召/特召成功时抽 1）。
            //   洗回卡组再抽回来只能靠 ① 自跳，那条「通召 + 抽 1」的路就没了，等于白扔一个召唤权。
            //   （手上有别的可洗卡时照常发，卡萝尔本身已由 `HandCardValue` 排到最后，不会被挑中。）
            if (Bot.Hand.All(c => c == null || c.IsCode(CardId.Carol)))
            {
                Log("未眠 ② → 手上只有卡萝尔可洗，留着给她通召抽1（洗回去等于自断通召点）");
                return false;
            }

            // ★ v5.42 用户口径：「未眠之城可以把手牌的本家怪兽回卡组顶，以达到下回合干扰效果。」
            //   但引擎里 ●1 的那次抽 1 是**强制的**（`c72978038.lua` 的 `s.drop` 直接 `Duel.Draw`，
            //   没有「可以」），而抽 1 抽到的正是刚压上去的那张 ——
            //     · 放**卡组顶** → 抽回自己，牌堆顶回到原样（群星置顶那张不动）→ 这个动作是安全的；
            //     · 放**卡组底** → 抽 1 会把群星置顶的那张抽走 → 下回合的干扰直接没了。
            //   ⇒ hold 期间只在「手里确实有一张本家能压回卡组顶」时才发；只能放底时留着不发。
            if (_holdMode && !CanCityTopAMoonCard())
            {
                Log("未眠 ② → 水母 hold 且手里没有能压回卡组顶的本家 → 这次不发（放底会抽走置顶那张）");
                return false;
            }

            AI.SelectOption(0);
            _returnHandPending = true;
            _returnHandIsCity = true;    // ★ v5.22：这次的返卡可以放顶/底，由 PickCityReturnCard 决定
            Log("未眠 ② 回 1 抽 1" + (_holdMode ? "（水母 hold → 把手里本家压回卡组顶，留给下回合干扰）" : ""));
            return true;
        }

        // ★ v5.43：副葬和未眠一起起手时，这一回合只让副葬干活 —— 未眠 ③ 会把墓地·除外的本家全洗回卡组，
        //   副葬堆下去的辉煌活不过这个结束阶段。判据只两件事：这张是不是还在手上 + 副葬本回合发过 / 还在手里。
        private bool CityDeferToBurial()
        {
            if (Card == null || Card.Location != CardLocation.Hand) return false;  // 已经在魔陷区的那张照常处理
            if (Duel.Player != 0) return false;                                    // 只在自回合（对方回合没有副葬可发）
            if (_burialUsedThisTurn) return true;                                  // 副葬本回合发过 → 墓地里有辉煌，别再发这张去洗
            return Bot.Hand.Any(c => c != null && c.IsCode(CardId.Burial));         // 副葬还在手里 → 先让它发
        }

        // hold 期间未眠② 能不能如愿「把一张本家压回卡组顶」—— 与 `PickCityReturnCard` 里**选放顶**的那几档同源：
        // 菈碧 / 帐 / 希耶娜 / 库露雪（选到任何一个都会走 `SEQ_DECKTOP`，抽 1 抽回来的是它自己，置顶那张不动）。
        private bool CanCityTopAMoonCard()
        {
            return Bot.Hand.Any(c => c != null
                && (c.IsCode(CardId.Rabi) || c.IsCode(CardId.Siero)
                    || c.IsCode(CardId.Hiena) || c.IsCode(CardId.Kurusu)));
        }

        // ★ v5.23：这回合还有没有路把大姐（一掷乾坤）摆到 EX 区 —— 有就先压着未眠② 等它。
        //   只认两种**确定**能凑出 2 只 7 星本体的情况，避免「等一个永远不来的大姐」把这回合的② 白扔：
        //     ① 场上已经站着 2 只可叠的 7 星 → `XyzJackpot` 下一个空闲询问就会叠掉（这里只是兜底，
        //        正常时序上 SpSummon 段排在 Activate 段之前，根本问不到 CityActivate）；
        //     ② 场上 1 只 + 手上有帐（西艾萝，② 返 1 张手卡自跳，所以手牌要 ≥2）→ 下一只补上来就够。
        //   排除抢血局：那 2 只 7 星要留给千金大奖 → 黑智天至 → 光子咆哮的斩杀线，不会拿去叠大姐。
        private bool CanBuildJackpotThisTurn()
        {
            if (Duel.Player != 0) return false;                     // 只在自己回合讲这个顺序
            if (Duel.Phase != DuelPhase.Main1 && Duel.Phase != DuelPhase.Main2) return false;  // 起动效果只能主阶发
            if (HasSpellcasterXyz()) return false;                  // 已经有大姐了，走白抽分支
            if (RaceMode()) return false;                           // 抢血局：7 星留着走斩杀线
            if (FreeEmzCount() == 0) return false;                  // 没有空 EX 区，大姐落不下来
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Jackpot))) return false;
            if (Bot.GetMonstersInExtraZone().Any(c => c.IsCode(CardId.Jackpot))) return false;

            int bodies = Bot.GetMonstersInMainZone()
                .Count(c => c != null && c.IsFaceup() && c.Level == 7 && !c.HasType(CardType.Xyz));
            if (bodies >= 2) return true;
            if (bodies >= 1
                && Bot.Hand.Count(c => c != null) >= 2
                && Bot.Hand.Any(c => c.IsCode(CardId.Siero))) return true;
            return false;
        }

        // ★ v5.22 用户口径（replay _LastReplay 23:00 局）：未眠② 洗回的那张卡**马上就会被自己抽回来**，
        //   所以该挑的不是「手里最没用的那张」，而是「抽回来最赚的那张」：
        //     ① 菈碧 → 抽到即自跳，而且 ② 自跳时再抽 1（一手换一怪 + 一张牌，全卡组最赚的一档）；
        //     ② 西艾萝（帐）→ 抽到即自跳，场上多一个 2100/2100，之后 ③ 还是抽卡引擎；
        //     ③ 希耶娜 / 库露雪 → 后攻或 T3 要解场时，抽到即自跳 → ② 直接弹掉对方一只怪 / 一张魔陷；
        //     ④ 以上都没有 → 把手坑沉到**卡组最下面**（洗到顶等于下回合又抽到它，白占一个抽卡位）。
        //   卡萝尔永远不进这条链 —— 见 `CityActivate` 里的说明（留着通召）。
        //   旧行为（v5.21 及以前）一律按 `HandCardValue` 取最低分：挑中的常常是菈碧 / 手坑，
        //   却统一丢到卡组最上面 —— 菈碧倒是能自跳，手坑则等于白洗一次、下回合又摸回来。
        private ClientCard PickCityReturnCard(List<ClientCard> hand)
        {
            if (hand == null || hand.Count == 0) return null;

            // ① 菈碧
            ClientCard rabi = hand.FirstOrDefault(c => c.IsCode(CardId.Rabi));
            if (rabi != null)
            {
                _returnHandToTop = true;
                Log("未眠 ② → 洗回菈碧到卡组最上面（抽回来 ① 自跳 + ② 再抽 1）");
                return rabi;
            }

            // ② 西艾萝（帐）
            ClientCard siero = hand.FirstOrDefault(c => c.IsCode(CardId.Siero));
            if (siero != null)
            {
                _returnHandToTop = true;
                Log("未眠 ② → 洗回西艾萝到卡组最上面（抽回来 ① 自跳）");
                return siero;
            }

            // ③ 解场档：未眠② 是 IGNITION，只在自己回合的主阶发，所以这里就是「升月要拆对方什么」。
            //    对方前场有大怪 / 后场没东西 → 希耶娜弹怪兽；对方后场有魔陷 → 库露雪弹魔陷。
            ClientCard hiena = hand.FirstOrDefault(c => c.IsCode(CardId.Hiena));
            ClientCard kurusu = hand.FirstOrDefault(c => c.IsCode(CardId.Kurusu));
            int enemyMon = Enemy.GetMonsters().Count;
            int enemySt = Enemy.GetSpells().Count;
            bool bigMon = Enemy.GetMonsters().Any(c => c != null && c.IsFaceup() && c.Attack >= 2000);

            if (hiena != null && enemyMon > 0 && (bigMon || enemySt == 0))
            {
                _returnHandToTop = true;
                Log("未眠 ② → 洗回希耶娜到卡组最上面（抽回来 ① 自跳 → ② 弹对方怪兽）");
                return hiena;
            }
            if (kurusu != null && enemySt > 0)
            {
                _returnHandToTop = true;
                Log("未眠 ② → 洗回库露雪到卡组最上面（抽回来 ① 自跳 → ② 弹对方魔陷）");
                return kurusu;
            }
            if (hiena != null && enemyMon > 0)
            {
                _returnHandToTop = true;
                Log("未眠 ② → 洗回希耶娜到卡组最上面（抽回来 ① 自跳 → ② 弹对方怪兽）");
                return hiena;
            }

            // ④ 兜底：手坑沉到卡组最下面
            ClientCard trap = hand.Where(c => HandTrapIds.Contains(c.Id))
                .OrderBy(HandCardValue).FirstOrDefault();
            if (trap != null)
            {
                _returnHandToTop = false;
                Log("未眠 ② → 手坑 " + trap.Name + " 沉到卡组最下面（防止再抽到）");
                return trap;
            }

            return null;   // 交给 HandCardValue 兜底（`_returnHandToTop` 保持 false = 放底）
        }

        // ================= 愚蠢的副葬 =================
        // 35726888：从卡组把 1 张魔法·陷阱卡送去墓地。
        //
        // ★ 本卡组唯一值得送的目标是「辉煌」：
        //     副葬（1 张）→ 辉煌进墓 → 辉煌②「除外自身 → 自己抽 1」
        //   等于「副葬 = 花 1 张手牌抽 1 张」，本身不赚；但本卡组 6 只 Lv7 全是
        //   「被抽到时 → 给对方看 → 自跳」，所以这一抽往往直接把场面铺开 ——
        //   在没有检索的卡组里，这就是最缺的那个启动点。
        //   相比之下送「群星」看着也行（墓地② 除外也能抽），但它没大姐在场时是
        //   「双方各抽 1」，白喂对手一张；而群星正常抽上手还能用 ① 置顶，价值高得多，
        //   不该被副葬消耗掉。
        //
        // ★ 一回合一次的限制是挂卡名的：本回合辉煌② 的名额已经用掉时，送下去的
        //   辉煌这回合抽不了。尤其场上有『未眠之城』时，它 ③ 结束阶段会把墓地本家
        //   全部洗回卡组 —— 送下去来不及用就白白被洗走，所以这张副葬留着下回合再发。
        private bool BurialActivate()
        {
            // ★ v5.42 用户口径：「副葬可以继续堆墓辉煌（但是不抽卡）」 —— v5.41 那条
            //   `if (_holdMode) return false;` 撤销。副葬只从**卡组**送墓，不从手牌上怪，不喂水母；
            //   这回合不抽是因为辉煌② 那条执行器仍被 hold 闸门挡着（`SplendorActivate` 不在白名单），
            //   堆下去纯粹为下回合的墓地/回收做准备。
            if (_splendorGraveUsed)
            {
                Log("副葬 本回合辉煌②已用 → 留着下回合");
                return false;
            }
            _burialPending = true;
            _burialUsedThisTurn = true;   // v5.2：记下「本回合副葬已发」，供群星置顶/抽卡的顺序判断
            bool waitStars = Bot.Hand.Any(c => c != null && c.IsCode(CardId.Stars));
            Log(_holdMode
                ? "副葬 送辉煌进墓（水母 hold → 只堆墓，这回合不抽，辉煌② 留到下回合）"
                : (waitStars
                    ? "副葬 送辉煌进墓（手里有群星 → 先不抽，等置顶后再抽）"
                    : "副葬 送辉煌进墓（接着用辉煌② 抽1）"));
            return true;
        }

        // ================= 超量：EX 区长线 =================

        private bool XyzJackpot()
        {
            // 一掷乾坤：7 星「盈彩月夜」×2，且只有落在 EX 区才有全抗 / 把对手检索换成对手抽 1
            // ★ 没有空 EX 区就不做 —— 大姐落在主要怪兽区只是一只 2100 白板，还占掉斩杀基底的位置
            if (FreeEmzCount() == 0) return false;
            // ★ EX 区最多 1 只：大姐那三条都是「这张卡」自身的，第二只不叠加封锁，
            //   白白占掉 EX 区（全抗位 / 斩杀基底位）。剩下的 R7 一律交给千金大奖。
            if (Bot.GetMonstersInExtraZone().Any(c => c.IsCode(CardId.Jackpot))) return false;
            List<ClientCard> mats = Bot.GetMonstersInMainZone()
                .Where(c => ArchetypeL7.Contains(c.Id) && c.Level == 7).ToList();
            if (mats.Count < 2) return false;
            AI.SelectMaterials(PickMaterials(mats, 2));
            Log("超量 一掷乾坤（EX 区）");
            return true;
        }

        // ★ v5.15 用户口径（优化16-2）：「场上已经有**大姐（一掷乾坤）+ 千金大奖**的时候，
        //   场上的菈碧和任意本家 7 星**不要再继续叠超量**，留在对方回合加速超量做阻抗 ——
        //   加速哪只看情况：第二张千金大奖弹卡、对方主打墓地就做 No.76、场上有 3 只 7 星还能做托勒密。」
        //
        //   为什么这对：先攻把大姐 + 千金大奖摆好之后，再叠第三只超量只是把「两种干扰」换成「同一只怪」，
        //   而对方回合的加速是**多出来的一次**超量（翼 ③ 不占自己回合的动作），
        //   素材留在场上还能挡刀、对方打过来时再决定叠谁。见 PreferredAccelXyz。
        private bool TerminalDuoReady()
        {
            bool jackpot = Bot.GetMonstersInExtraZone().Any(c => c.IsCode(CardId.Jackpot))
                || Bot.GetMonstersInMainZone().Any(c => c.IsCode(CardId.Jackpot));
            bool bounty = Bot.GetMonstersInMainZone().Any(c => c.IsCode(CardId.Bounty));
            return jackpot && bounty;
        }

        private bool XyzBounty()
        {
            // 千金大奖：7 星 ×2 只以上，落哪都行 —— 主要怪兽区的默认选择，也是斩杀线的基底
            // ★ v5.15：两者已在场 → 停手，把 7 星留给对方回合的加速（本回合能收人头时照旧，见 CanFinish）。
            if (TerminalDuoReady() && !CanFinish())
            {
                Log("超量 千金大奖 → 已有大姐+千金大奖，7星留着对方回合加速（不再叠）");
                return false;
            }
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 2) return false;
            AI.SelectMaterials(PickMaterials(mats, 2));
            Log("超量 千金大奖（主要怪兽区）");
            return true;
        }

        // ================= 主要怪兽区空间闸门（v5.7）=================
        //
        // ★ 用户口径（replay/优化9.yrp，13:24 截图：升月主要怪兽区只剩 2 格空闲）：
        //   自己回合空闲主要怪兽区 ≤2 格时，场上的 7 星本家要**先叠一次超量**把格子腾出来，再继续抽卡。
        //
        //   为什么必须这样：抽卡链自己会把自己堵死。
        //     c11333460（雅）/ c47828069（翼）/ c75934716（帐）的「被抽到时自跳」是 EVENT_DRAW 诱发，
        //     落地后各自的「登场成功 → 抽 1」再触发一次抽卡（雅③ / 翼② / 帐③）：
        //       抽 1 → 本家 ① 自跳（占 1 格）→ ③/② 再抽 1 → 又抽到本家 → 又自跳（再占 1 格）→ ……
        //     每一步都是新开一条连锁，**中间根本不回 idle cmd**，
        //     所以「腾格子」只能发生在这一波抽卡开始之前 —— 也就是 idle cmd 这一层。
        //     ★ 反过来说：格子占满那一刻再抽到的本家，跳不下来 → 它的抽卡效果永远不触发，
        //       这张卡烂在手里，一条链就这么断了（优化9 里就是这样把场面做到卡的）。
        //
        //   为什么单纯把这两条注册在最前面就够：
        //     OnSelectIdleCmd 的外层循环是「执行器 → 该执行器的各类动作」，执行器列表有序；
        //     抽卡全是 ExecutorType.Activate（下面第 1~3 段），超量是 ExecutorType.SpSummon。
        //     把这条 SpSummon 放到列表最前，每次 idle cmd 都会先问「要不要先腾格子」，
        //     return false 才轮到后面的抽卡执行器 —— 这就是「抽卡前先腾」的实现方式。
        //
        //   腾哪一只（越靠前越省格子）：
        //     · EX 区空着、大姐还没在场上 → 「一掷乾坤」：2 只 7 星 → 1 只落 EX 区，主要怪兽区一次空出 **2 格**；
        //     · 否则 → 千金大奖：2 只 7 星 → 1 只留主要怪兽区，空出 1 格，同时是斩杀线基底。
        //   素材沿用 PickMaterials（先甩雅，菈碧排最后 —— 它要在场上开 ③ 加速 R7）。
        //
        //   让位（EnemyXyzPlayPending）：巨眼 / 混沌大西洲 / 托勒密 是「和对方换资源」的解场线，
        //   同样吃 2~3 只 7 星、最后也只占 1 格，条件成立时优先交给它们 —— 符合总纲第 1 条。
        //   它们本来注册在后面，这里只是在「格子紧」这个交叉口补一次让位判断。
        private const int MainZoneTightFree = 2;   // 空闲主要怪兽区 ≤2 格 → 先腾格子再抽

        private int FreeMainZoneCount()
        {
            return 5 - Bot.GetMonstersInMainZone().Count;
        }

        private bool MainZoneTight()
        {
            return FreeMainZoneCount() <= MainZoneTightFree;
        }

        // 解场型超量的条件是否已经成立（成立就让位，别用千金大奖把素材吃掉）。
        // 判据与 XyzBigEye / XyzChaosAtlandis / XyzOmega7 自身的闸门保持一致，改那边记得同步这里。
        private bool EnemyXyzPlayPending()
        {
            if (Bot.ExtraDeck.Any(c => c.IsCode(CardId.BigEye)) && !_lightLock && StealTarget() != null)
                return true;   // 巨眼：对方站着值得抢的怪
            if (Bot.ExtraDeck.Any(c => c.IsCode(CardId.ChaosAtlandis))
                && Duel.Phase == DuelPhase.Main1 && !CanFinish() && EnemyWall() != null)
                return true;   // 混沌大西洲：对方有 ≤3200 的墙
            if (Bot.ExtraDeck.Any(c => c.IsCode(CardId.Omega7)) && !CanFinish()
                && (Enemy.GetMonsterCount() >= 3
                    || (Duel.Phase == DuelPhase.Main2 && Level7Bodies().Count >= 3)))
                return true;   // 托勒密：解场弹 3 / M2 三只 7 星换 1 格（腾 2 格）
            return false;
        }

        // 空闲 ≤2 格且 EX 区有空位 → 叠大姐（一次空出 2 格，且大姐本来就该在 EX 区）
        private bool CompactToExtraZone()
        {
            if (Duel.Player != 0) return false;                                  // 只在升月自己回合腾
            if (!MainZoneTight()) return false;                                  // 空闲 >2 格：还不用腾
            if (CanFinish()) return false;                                       // 本回合能收人头 → 别拿两只 7 星的打点换 1 只超量
            // ★ v5.16：抢血时这 2 只 7 星要留给千金大奖（→ 黑智天至 → 光子咆哮 5300），
            //   不能拿去 EX 区叠大姐 —— 录像 21:27:56 就是这么把整回合的打点换成 2100 的。
            //   腾格子的活交给下面那条 CompactToMainZone（千金大奖一样空出 1 格）。
            if (RaceMode()) return false;
            if (TerminalDuoReady()) return false;                                // v5.15：大姐+千金大奖已在场 → 7 星留着对方回合加速
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Jackpot))) return false;
            if (FreeEmzCount() == 0) return false;                               // 没有空 EX 区，大姐只能落主要怪兽区（那就不如千金大奖）
            if (Bot.GetMonstersInExtraZone().Any(c => c.IsCode(CardId.Jackpot))) return false;
            if (EnemyXyzPlayPending()) return false;
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 2) return false;
            AI.SelectMaterials(PickMaterials(mats, 2));
            Log("超量 一掷乾坤（腾格：主要怪兽区空闲 " + FreeMainZoneCount() + " 格 → 落 EX 区，空出 2 格）");
            return true;
        }

        // 空闲 ≤2 格 → 叠千金大奖（空出 1 格，继续抽卡不会卡场）
        private bool CompactToMainZone()
        {
            if (Duel.Player != 0) return false;
            if (!MainZoneTight()) return false;
            if (CanFinish()) return false;                                       // 同上：能收人头就先打，不腾
            if (TerminalDuoReady()) return false;                                // v5.15：大姐+千金大奖已在场 → 7 星留着对方回合加速
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Bounty))) return false;
            // EX 区还有位、大姐还没在场上 → 让 CompactToExtraZone 先做（它能空出 2 格）。
            // 正常情况下上面那条已经 return true 了，这里只是顺序被改动时的兜底。
            // ★ v5.16：抢血时不再给大姐让路 —— 反正 CompactToExtraZone 也被 RaceMode 挡住了，
            //   再跳一次就变成「谁都不叠」，格子照样卡死。
            if (!RaceMode()
                && FreeEmzCount() > 0
                && !Bot.GetMonstersInExtraZone().Any(c => c.IsCode(CardId.Jackpot))
                && Bot.ExtraDeck.Any(c => c.IsCode(CardId.Jackpot))) return false;
            if (EnemyXyzPlayPending()) return false;
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 2) return false;
            AI.SelectMaterials(PickMaterials(mats, 2));
            Log("超量 千金大奖（腾格：主要怪兽区空闲 " + FreeMainZoneCount() + " 格 → 空出 1 格）");
            return true;
        }

        // ================= 超量：针对性终端 =================
        // 用户给的五只额外怪各有明确对口场景，不是「谁都能出」：
        //   巨眼      ← 对方场上站着高价值怪兽（高攻 / 有干扰）→ 直接抢过来
        //   混沌大西洲 ← 对方场上 ≤3200 攻的抗性怪（不受效果 / 不能成为对象 / 不会被破坏）→ 3300 碾过去
        //   托勒密    ← 解场弹 3 / M2 站场让对方回合弹 3
        //   No.76     ← 对方主打墓地（挖走墓地怪兽）
        //   食兽      ← 对方主打魔法展开（闪刀姬那类速攻魔法循环）

        // 主要怪兽区里「能当超量素材的 7 星本体」。
        // ★ 必须排除超量怪兽 —— 超量怪兽不能作为超量素材，以前的写法只看 Level==7
        //   会把已经躺在场上的千金大奖/大姐也算进来，被引擎过滤掉之后素材就凑不够了。
        private List<ClientCard> Level7Bodies()
        {
            return Bot.GetMonstersInMainZone()
                .Where(c => c.IsFaceup() && c.Level == 7 && !c.HasType(CardType.Xyz))
                .OrderBy(MaterialRank)
                .ToList();
        }

        private int MyBestAttack()
        {
            return Bot.GetMonsters().Where(c => c.IsFaceup()).Select(c => c.Attack).DefaultIfEmpty(0).Max();
        }

        // No.11 巨眼（R7×2，DARK 魔法师族 2600/2000）
        //   ① 1 回合 1 次，拔 1 素材，以对方 1 只怪兽为对象 → 得到那只怪兽的控制权
        //      （发动当回合这张卡不能攻击）。
        //   ★ 抢「怪」不抢「魔陷」：对方后场贴的永续/场地它碰不到，那种局面仍然交给千金大奖弹卡。
        //   ★ 只在对方那只怪比我方任何怪都值钱时出 —— 2600 抢个 1500 杂鱼等于白扔两个素材。
        private bool XyzBigEye()
        {
            if (Duel.Player != 0) return false;
            if (_lightLock) return false;                       // DARK 终端，光属性超量自肃期间不能用
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.BigEye))) return false;
            ClientCard prey = StealTarget();
            if (prey == null) return false;
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 2) return false;
            AI.SelectMaterials(mats.Take(2).ToList());
            Log("超量 巨眼（夺取 " + prey.Name + " ATK" + prey.Attack + "）");
            return true;
        }

        // 值得抢的怪：比我方最强怪还大的，或者本身就是「麻烦卡」（封锁 / 干扰 / 无敌抗性）。
        private ClientCard StealTarget()
        {
            int myBest = MyBestAttack();
            ClientCard problematic = Util.GetProblematicEnemyMonster();
            return Enemy.GetMonsters()
                .Where(c => c.IsFaceup() && c.Attack > 0)
                .Where(c => c.Attack > myBest || c.Attack >= 2500 || c == problematic)
                .OrderByDescending(c => c.Attack)
                .FirstOrDefault();
        }

        // 混沌No.6 先史遗产 混沌大西洲巨人（R7×3，LIGHT 机械族 3300/3300）
        //   ① 1 回合 1 次，以对方 1 只怪兽为对象 → 那只怪当作「攻击力上升 1000 的装备魔法」给这张卡装备。
        //   ★ 用途：对方摆出一只「打不过又拆不掉」的抗性怪，用 3300 的本体直接顶上去
        //     （用户给的门槛：不高于 3200 攻）。
        //   ★ 硬代价：「这个卡名的①②的效果发动过的回合，对方受到的全部伤害变成 0」
        //     → 这回合基本别想打人。所以只在「本回合收不掉对面」时出，纯解场，伤害留给下回合。
        //   ★ ② 要求素材里有「No.6 大西洲巨人」，本卡组从不拿它当素材 → ② 永远用不了，不实现。
        private bool XyzChaosAtlandis()
        {
            if (Duel.Player != 0) return false;
            if (Duel.Phase != DuelPhase.Main1) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.ChaosAtlandis))) return false;
            if (CanFinish()) return false;                      // 能斩杀就别上这张（当回合打不出伤害）
            ClientCard wall = EnemyWall();
            if (wall == null) return false;
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 3) return false;                   // R7×3
            AI.SelectMaterials(mats.Take(3).ToList());
            Log("超量 混沌大西洲（解 " + wall.Name + " ATK" + wall.Attack + "）");
            return true;
        }

        // 对方场上「我方处理不掉的墙」：攻击力 ≤ 3200（3300 本体打得过），
        // 且要么比我方任何怪都硬，要么是引擎认定的麻烦卡。
        private ClientCard EnemyWall()
        {
            int myBest = MyBestAttack();
            ClientCard problematic = Util.GetProblematicEnemyMonster();
            return Enemy.GetMonsters()
                .Where(c => c.IsFaceup() && c.Attack > 0 && c.Attack <= 3200)
                .Where(c => c.Attack > myBest || c == problematic)
                .OrderByDescending(c => c.Attack)
                .FirstOrDefault();
        }

        // 超神星辉士 星圣神龙 托勒密星团Ω7（R7×3，LIGHT 机械族 2700/2700）
        //   ② 自己·对方回合 1 次，拔任意数量素材，以那个数量的对方场上**怪兽**为对象 → 那些怪兽回卡组。
        //      ★ 是「回卡组」不是破坏：抗性、遗言、墓地回收全部一并作废，解场端最干净的一手。
        //   ① 要墓地「星骑士」7 种类以上 —— 本卡组没有星骑士，这条等于白板，不看。
        //   召唤方式也只有一个：R7×3 常规超量（那个「在星骑士/星圣超量怪上重叠」的捷径我们没有素材）。
        //
        // 两种用法：
        //   1) 自己回合主阶 1：对方 ≥3 只怪兽且本回合收不掉 → 拔 3 弹 3 清干净；
        //   2) 自己回合主要阶段 2：三只 7 星已经打完，留着也打不动了 → 换托勒密站住，
        //      下个对方回合弹 3 只，等于一个持续的干扰位。
        private bool XyzOmega7()
        {
            if (Duel.Player != 0) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Omega7))) return false;
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 3) return false;                   // R7×3

            if (Duel.Phase == DuelPhase.Main2)
            {
                // 战阶已经结束，这三只 7 星本回合的价值已经榨干了
                AI.SelectMaterials(mats.Take(3).ToList());
                Log("超量 托勒密Ω7（M2 压制：下个对方回合弹 3 只）");
                return true;
            }
            if (Duel.Phase == DuelPhase.Main1 && Enemy.GetMonsterCount() >= 3 && !CanFinish())
            {
                AI.SelectMaterials(mats.Take(3).ToList());
                Log("超量 托勒密Ω7（解场：弹 3 只回卡组）");
                return true;
            }
            return false;
        }

        // No.76 谐调光师 渐变天乐斗士（R7×2，LIGHT 天使族 2700/2100）
        //   ③ 自己·对方回合，以对方墓地 1 只怪兽为对象，拔 1 素材 → 把那只怪当作这张卡的超量素材。
        //      ★ 把对方墓地的关键怪兽挖走：既断「墓地起跳 / 墓地回收」，又顺手给自己加素材。
        //   ★ 用户定位：专门打「主打墓地效果」的卡组，属于看对局才出的针对卡。
        //     所以门槛往高里放：对方墓地 ≥3 只怪兽、而且前场已经空了（有怪时千金大奖/巨眼更实用）。
        //
        // ★ v4.8 用户修正：这个门槛以前只看「对方墓地 ≥3 只怪兽」，太松 ——
        //   对面随手丢 3 张手坑（灰流丽 / 幽鬼兔 / 遮蒙者 / G / 水母）进墓也满足，
        //   于是升月先攻、根本不知道对面什么卡组时，就把两只 7 星烧成一只 2700 的 No.76 白板。
        //   用户口径：**一般局面一律守备表示出「望·斯奎兹-千金大奖」（1400/2800）**，
        //   No.76 只在确认对面吃墓地时才出。现在三条必须同时成立：
        //     ① 不是局面早期（Duel.Turn ≤ 2）—— 先攻 / 后手第一回合信息不足，不赌；
        //     ② 对方墓地里的**非手坑**怪兽 ≥ 3（手坑进墓不算信号，见 HandTrapIds）；
        //     ③ 对方已经露出墓地利用的马脚（`_enemyGravePlay`：从墓地发动效果 / 从墓地回场），
        //        或者非手坑怪兽 ≥ 4（堆得明显不正常）。
        private bool XyzUtopia76()
        {
            if (Duel.Player != 0) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Utopia76))) return false;

            if (Duel.Turn <= 2) return false;   // ① 局面早期不赌

            // ② 手坑不算墓地卡组的信号
            int graveMonsters = Enemy.GetGraveyardMonsters()
                .Count(c => c != null && !HandTrapIds.Contains(c.Id));
            if (graveMonsters < 3) return false;

            // ③ 需要「对方真的在用墓地」的硬证据；只有堆得特别多（≥4）才免检
            if (!_enemyGravePlay && graveMonsters < 4) return false;

            if (Enemy.GetMonsterCount() > 0) return false;
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 2) return false;
            AI.SelectMaterials(mats.Take(2).ToList());
            Log("超量 No.76（对方墓地 " + graveMonsters + " 只非手坑怪"
                + (_enemyGravePlay ? " · 已暴露墓地利用" : " · 堆墓异常") + " → 挖墓）");
            return true;
        }

        private bool XyzChersib()
        {
            // 漆梏之食兽（R7×2+，DARK 兽族 700+素材×700 / 2500）
            //   ② 对方发动通常·速攻魔法时 → 场上的那张卡吸成自己的超量素材，且这张卡不受那个效果影响。
            //   ③ 结束阶段把素材里的魔法卡在自己场上盖回来。
            //
            // ★ v5.11 用户口径（重新装回卡组后给的边界）：
            //   「如果对方卡组通常魔法和速攻魔法卡用得多的话可以出，其他情况就不出这卡了
            //     （比如对方是闪刀卡组和光与暗的仪式卡组）。
            //     此外，如果对方后场盖放的魔法陷阱卡很多的话，升月 R7 应该优先出千金大奖来弹回卡组。」
            //   ⇒ 判据从 v5.10 的「对方魔陷区 ≥2 张」改成两条：
            //     1) 对方**墓地**里的「通常魔法 / 速攻魔法」≥3 张 —— 食兽 ② 只对这两种响应，
            //        所以只看这两种的坟场数量才是「对方真的在刷通常·速攻」的证据（闪刀的速攻、仪式的通常都算）；
            //        原来数「对方魔陷区张数」会把**盖着的陷阱**也算进来 → 陷阱卡组被误判成魔法卡组。
            //     2) 对方后场**盖卡 ≥2 张时不抢** —— 那种局面千金大奖「拔 2 弹 2」把盖卡弹回卡组更赚。
            //   ★ 同时删掉 v5.3 以前的「千金大奖用尽 → 第 4 只 R7」兜底：用户口径「其他情况就不出这卡」。
            if (_lightLock) return false;                       // DARK，光属性超量自肃期间不能用
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Chersib))) return false;
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 2) return false;

            int setCards = Enemy.GetSpells().Count(c => c != null && !c.IsFaceup());
            if (setCards >= 2)
            {
                Log("食兽 → 对方后场盖卡 " + setCards + " 张，让千金大奖拔2弹2");
                return false;
            }

            int nq = Enemy.GetGraveyardSpells().Count(IsNormalOrQuickSpell);
            if (nq < 3)
            {
                Log("食兽 → 对方墓地只有 " + nq + " 张通常·速攻魔法，不是魔法循环卡组，不出");
                return false;
            }

            AI.SelectMaterials(mats.Take(2).ToList());
            Log("超量 漆梏之食兽（对方墓地通常·速攻魔法 " + nq + " 张 → 吸魔法）");
            return true;
        }

        // 「通常魔法 / 速攻魔法」判定。
        //   ★ cards.cdb 里通常魔法只带 TYPE_SPELL（大岚 type=0x2，**没有** TYPE_NORMAL），
        //     所以「通常魔法」= 魔法卡且不带任何子类型；速攻魔法单独有 TYPE_QUICKPLAY。
        private static bool IsNormalOrQuickSpell(ClientCard c)
        {
            if (c == null || !c.HasType(CardType.Spell)) return false;
            if (c.HasType(CardType.QuickPlay)) return true;
            return !c.HasType(CardType.Continuous) && !c.HasType(CardType.Equip)
                && !c.HasType(CardType.Field) && !c.HasType(CardType.Ritual);
        }

        // ================= 征霸龙-烈龙（v5.10）=================
        //
        // 征霸龙-烈龙 47132793（炎属性龙族 R7×2，2800/1800）
        //   ① 1回合1次，拔 1 个超量素材 → **自己的手卡·场上 1 张卡 + 对方场上 1 张卡破坏**。
        //      IGNITION（只能自己主阶发动），两段选卡：先「自己的手卡+场上」，再「对方场上」。
        //   ② 墓地自跳：除外 2 只龙族/炎属性 —— 本卡组一张都没有，永远用不了，不注册。
        //
        // ★ 用户口径（v5.10，replay 优化10 之后那局）：
        //   「这张卡是升月在 t2 或者 t3 需要解掉对方场上某张卡（经常是后场的魔法陷阱卡或者场地）时出的。
        //     对方场上一张公开的表侧表示场地，很明显对方的资源依赖这张场地，所以这时应该出新超量来破坏这张卡。
        //     新超量的效果选择自己的卡的对象最好选择大姐 —— 因为大姐不受其他卡的效果影响，所以能白嫖一张卡。」
        //
        // ★ 为什么「选大姐」是白嫖：大姐（一掷乾坤）在 EX 区时带 EFFECT_IMMUNE_EFFECT（SetValue 1，
        //   条件 GetSequence()>4）→ `Duel.Destroy` 里她那张破坏失败，而对方那张照常破坏。
        //   所以自己那张的优先级：EX 区大姐（代价 0）→ 手牌菈碧 / 开不了的场地（代价 1~2）→ 其余。
        //
        // ★ 为什么它是「拆魔陷/场地」的位置：前面四只针对终端只处理怪兽
        //   （巨眼抢怪 / 大西洲碾抗性怪 / 托勒密弹怪 / No.76 挖墓），没人管后场和场地；
        //   千金大奖 ② 虽然也能弹魔陷，但要吃满素材且会把 `_bountyActivated` 打上（断掉黑智天至的斩杀线）。
        private bool XyzBlazeDragon()
        {
            if (Duel.Player != 0) return false;                    // ① 是 IGNITION：只有自己回合的主阶能用
            if (Duel.Phase != DuelPhase.Main1) return false;        // 主阶 1 出 → 2800 还能进战阶打；M2 留给天霆号
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.BlazeDragon))) return false;
            if (CanFinish()) return false;                          // 能收人头就先把伤害打出去，不为拆卡浪费素材
            List<ClientCard> mats = Level7Bodies();
            if (mats.Count < 2) return false;

            ClientCard prey = BlazePrey();
            ClientCard sac = PickBlazeSacrifice();
            if (prey == null || sac == null) return false;
            if (!BlazeWorthIt(prey, sac)) return false;

            AI.SelectMaterials(PickMaterials(mats, 2));
            Log("超量 征霸龙（拆对方「" + prey.Name + "」，自己那头用「" + sac.Name + "」）");
            return true;
        }

        // ① 的发动：条件与召唤时同一条换账，只是状态可能变了（比如对方又盖了一张），所以重新算一次。
        private bool BlazeActivate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            if (EffectNegated()) return false;                      // 被无效还发 = 白扔 1 个素材
            if (!Card.HasXyzMaterial(1)) return false;
            if (Duel.Player != 0) return false;                     // IGNITION：只在自己回合
            ClientCard prey = BlazePrey();
            ClientCard sac = PickBlazeSacrifice();
            if (prey == null || sac == null) return false;
            if (!BlazeWorthIt(prey, sac)) return false;

            _blazeSacrifice = sac;
            _blazePrey = prey;
            _blazePending = 2;                                       // 紧接着两次选卡：自己的 → 对方的
            Log("征霸龙 ① 拔1 → 破坏自己「" + sac.Name + "」+ 对方「" + prey.Name + "」");
            return true;
        }

        // 对方场上最值得拆的那张。
        //   场地魔法（对方的资源引擎，用户点名的场景）＞ 表侧永续/装备 ＞ 表侧其他魔陷 ＞ 里侧盖卡。
        //   ★ 「场上」包含场地魔法区：大岚这类卡用的也是 LOCATION_ONFIELD，引擎会把它算进来；
        //     WindBot 这边它落在 `SpellZone[5]`（GetFieldSpellCard），所以 GetSpells() 里能拿到。
        private ClientCard BlazePrey()
        {
            return BlazePreyIn(Enemy.GetMonsters().Concat(Enemy.GetSpells()).Where(c => c != null).ToList());
        }

        private ClientCard BlazePreyIn(IEnumerable<ClientCard> cards)
        {
            return cards.Where(c => c != null)
                .OrderByDescending(BlazePreyValue)
                .FirstOrDefault();
        }

        private int BlazePreyValue(ClientCard c)
        {
            if (c == null) return -1;
            if (c.Location == CardLocation.FieldZone || c.HasType(CardType.Field)) return 100;  // 场地：对方的资源引擎
            if (!c.IsFaceup()) return 30;                                                       // 里侧：价值未知
            if (c.HasType(CardType.Continuous)) return 70;                                      // 永续：一直在生效
            if (c.HasType(CardType.Equip)) return 65;
            if (c.HasType(CardType.Spell) || c.HasType(CardType.Trap)) return 60;
            return 50;                                                                          // 怪兽（一般轮不到它，前四只会先处理）
        }

        // 自己那张要破坏谁：从「手卡 + 自己场上（含 EX 区）」里挑代价最小的。
        private ClientCard PickBlazeSacrifice()
        {
            return Bot.Hand.Where(c => c != null)
                .Concat(Bot.GetMonsters())        // 含 EX 区（大姐在 EX 区免疫 → 代价 0，天然排第一）
                .Concat(Bot.GetSpells())          // 含场上魔法陷阱 + 场地
                .Where(c => c != null && !c.IsCode(CardId.BlazeDragon))   // 别把自己炸了
                .OrderBy(BlazeSacrificeCost)
                .FirstOrDefault();
        }

        // 代价估值（越低越愿意拿它去换）：
        //   0 = EX 区大姐（免疫，破坏不掉 → 白嫖）
        //   1 = 手牌菈碧（纯白板，只有「被抽到」才能用）
        //   4 = 自己场上/手牌的其他魔陷
        //   5~7 = 场上的怪兽 / 超量终端（少一只就是实打实的场面损失，尽量不动）
        //   9 = 手牌里其他本家（丢手抽卡 / 返卡自跳的引擎）
        private int BlazeSacrificeCost(ClientCard c)
        {
            if (c == null) return 99;
            if (JackpotImmune(c)) return 0;
            if (c.Location == CardLocation.Hand)
            {
                if (c.IsCode(CardId.Rabi)) return 1;
                if (ArchetypeL7.Contains(c.Id)) return 9;
                return 8;
            }
            if (c.Location == CardLocation.SpellZone || c.Location == CardLocation.FieldZone) return 4;
            if (c.HasType(CardType.Xyz)) return 7;      // 场上的超量 = 终端，别当祭品
            return 5;
        }

        // 这一手划不划算（总纲判据 2：交换必须是赚的）
        //   · 只拆**表侧正在生效**的魔陷 / 场地（值 ≥60）：怪兽交给巨眼/大西洲，里侧盖卡价值未知不值得专门出。
        //   · 对方前场 ≤1 只才做：站着 2 只以上时千金大奖「拔 2 弹 2」更赚，让它先上（它注册在后面）。
        //   · 自己那头：有大姐当免死牌（代价 0）→ 连里侧盖卡都能顺手拆；
        //     否则只允许拿手牌废牌去换（代价 ≤2，菈碧 / 开不了的场地）。
        private bool BlazeWorthIt(ClientCard prey, ClientCard sac)
        {
            int value = BlazePreyValue(prey);
            int cost = BlazeSacrificeCost(sac);
            bool targetOk = (cost <= 0 && value >= 30) || (cost <= 2 && value >= 60);
            bool boardOk = Enemy.GetMonsterCount() <= 1;
            bool worth = targetOk && boardOk;
            Log("征霸龙 换账：拆对方「" + prey.Name + "」(值 " + value + ")，自己那头「" + sac.Name
                + "」(损 " + cost + ")，对方前场 " + Enemy.GetMonsterCount() + " 只 → " + (worth ? "做" : "不做"));
            return worth;
        }

        // ================= 主要怪兽区：打点阶梯 =================
        //   千金大奖(1400) → 黑智天至(1600) → 光子咆哮(4500，战阶 5300) → 天霆号(3000，M2)
        // 全部是「在自己场上的超量怪兽上面重叠」的升阶。
        //
        // ★ 素材是累加的：procedure.lua 的叠放分支（e:GetLabel()==1）会把基底原有的 overlay 组
        //   整个搬给新卡，再把基底本身也叠上去。所以
        //     千金大奖 2 → 黑智天至 3 → 光子咆哮 4 → 天霆号 5
        //   天霆号「拔 2 清场」、光子咆哮「拔 3 无效全场」都吃得起。

        // 光子咆哮本体打点（主阶 1 的面板值）。
        private const int PhotonRoarAtk = 4500;

        // 黑智天至 ① 的加成：c:GetRank()*100。R8 → +800。
        // ★ 关键：`s.atkop` 注册的是 **EFFECT_TYPE_FIELD + EFFECT_UPDATE_ATTACK，Duel.RegisterEffect(e1, tp)**
        //   —— 效果挂在玩家上，不是挂在黑智天至身上；`SetTarget(LOCATION_MZONE,0)` + `s.atktg = IsType(TYPE_XYZ)`
        //   覆盖「自己场上所有超量怪兽」，`SetReset(RESET_PHASE+PHASE_END)` 持续到回合结束，
        //   `s.atkcon2 = Duel.IsBattlePhase()` 只在战斗阶段生效。
        //   ⇒ 黑智天至被叠走之后，加成依然在。光子咆哮战阶是 4500+800=5300。
        private const int IrisfilBuffR8 = 800;

        // 光子咆哮在战斗阶段的实际打点（叠在黑智天至上面之后）。
        private const int PhotonRoarBattle = PhotonRoarAtk + IrisfilBuffR8;   // 5300

        // 升阶链能摸到的最高打点 = 光子咆哮战阶 5300，用来判断「这一拳够不够收人头」。
        private const int PhotonRoarLethal = PhotonRoarBattle;

        // ================= v5.16 抢血模式 =================
        //
        // ★ 用户口径（replay「2026-10-24 21-27-04.yrp」）：
        //   「第一回合敌方空场，升月回合是第二回合，应该抢血才对。所以升月本家的怪兽都应该是攻击表示特殊召唤。
        //     另外抢伤害时候就要走斩杀路线了：拿两只怪兽叠 R7，再叠黑智天至，再叠超银河眼光子龙（4500 攻）；
        //     等银河眼战斗完，主要阶段 2 再叠重铠和天霆号。」
        //
        // 「抢血」和「能不能一击打死」是两件事（`CanFinish` 只管后者）。
        // 判定条件（缺一不可）：
        //   ① 升月自己回合 —— 对方回合没有「抢血」这回事，那是防守回合；
        //   ② 不是先攻首回合（T1 按规则进不了战斗阶段）；
        //   ③ 对方前场 ≤1 只 —— 铺开 2 只以上交给千金大奖弹卡 / 天霆号换账（v5.8 / v5.9 口径），
        //      那不是抢血，那是拆场；
        //   ④ 对方的墙拦不住 5300 —— 站着 5300 以上的怪时，爬阶梯只会撞墙，
        //      那种场面该交给混沌大西洲（≤3200 碾）/ 巨眼（抢）/ 托勒密（弹回卡组）。
        // 只在主要阶段 1 与战斗阶段生效：主要阶段 2 伤害已经打完了，这时出的天霆号 / 重铠要横着站。
        private bool RaceMode()
        {
            if (Duel.Player != 0) return false;
            if (Duel.Turn <= 1) return false;
            if (Enemy.LifePoints <= 0) return false;
            if (Duel.Phase != DuelPhase.Main1
                && Duel.Phase != DuelPhase.BattleStart
                && Duel.Phase != DuelPhase.BattleStep
                && Duel.Phase != DuelPhase.Damage
                && Duel.Phase != DuelPhase.DamageCal
                && Duel.Phase != DuelPhase.Battle) return false;
            if (Enemy.GetMonsterCount() > 1) return false;

            int wall = 0;
            foreach (ClientCard c in Enemy.GetMonsters())
            {
                if (c == null) continue;
                int a = c.IsFaceup() ? c.Attack : Math.Max(c.Attack, 2000);   // 盖怪按 2000 估
                if (a > wall) wall = a;
            }
            return wall < PhotonRoarBattle;
        }

        // 能不能一击打死？—— 只用来判断「现在该不该直接打」，也用来判断千金大奖该不该留着当基底
        private bool CanFinish()
        {
            // ★ 先攻第一回合（全局 T1）按规则不能进战斗阶段
            if (Duel.Turn == 1) return false;
            if (Enemy.LifePoints <= 0) return false;
            if (Enemy.LifePoints <= PhotonRoarLethal) return true;

            // 场面打点已经够了就别叠（先直接打）；不够但换成光子咆哮够 → 走斩杀线
            int atk = TotalAttack();
            return atk < Enemy.LifePoints && atk + LadderGain() >= Enemy.LifePoints;
        }

        private int TotalAttack()
        {
            int total = 0;
            foreach (ClientCard c in Bot.GetMonsters())
                if (c.IsFaceup()) total += c.Attack;
            return total;
        }

        // 把场上的千金大奖（1400）升成光子咆哮（战阶 5300）能多出来的伤害。
        // ★ 千金大奖本回合已经弹过卡 → 叠不成黑智天至（ovfilter 的 flag），这条增益直接归零。
        private int LadderGain()
        {
            if (_bountyActivated) return 0;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Irisfil))) return 0;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.PhotonRoar))) return 0;
            if (!Bot.GetMonsters().Any(c => c.IsCode(CardId.Bounty))) return 0;
            return PhotonRoarBattle - 1400;
        }

        // 黑智天至 伊里斯斐尔（R8，ATK 800）
        //   召唤：「8星怪兽×2」，另外「1回合1次也能在**这个回合没有在怪兽区域把效果发动的**
        //         自己场上的超量怪兽上面重叠来超量召唤」—— 注意不限阶级，R7 的千金大奖也能叠。
        //   ① 超量召唤成功 → **这个回合的战斗阶段，自己场上所有超量怪兽攻击力 + 自身阶级×100**
        //      （玩家侧 FIELD 效果，持续到结束阶段；黑智天至被叠走加成仍然在 → 光子咆哮战阶 5300）
        //   ② 被战斗·效果破坏时，拔 1 个超量素材代替
        //
        // ★ v3.3 定位修正：以前只在「刚好能一击斩杀」时才叠，结果 800 的黑智天至经常站着什么都不做，
        //   打不动人也留不住场。现在它是主要怪兽区打点阶梯的第一级，只要允许就往上爬。
        //   两条闸门保留：
        //     · 先攻 T1 不叠 —— 那一回合要的是千金大奖 2800 守备的墙，不是 1600；
        //     · 千金大奖本回合已经弹过卡 → ovfilter 的 flag 已打上，叠不了（BountyActivate 里已规避）。
        private bool IrisfilSummon()
        {
            if (Duel.Player != 0) return false;
            if (Duel.Phase != DuelPhase.Main1) return false;
            if (Duel.Turn == 1) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Irisfil))) return false;

            // 基底只认千金大奖：它才是「打点用完了可以叠走」的那只。
            // ★ 不要去叠托勒密 / 巨眼 / 混沌大西洲 —— 那几只的价值在它们自己的效果，
            //   叠成黑智天至等于把解场手段扔掉换 800 打点。
            ClientCard baseXyz = Bot.GetMonstersInMainZone()
                .FirstOrDefault(c => c.IsFaceup() && c.IsCode(CardId.Bounty));
            if (baseXyz == null) return false;

            // 基底本回合在怪兽区发过效果 → 黑智天至的召唤条件直接不成立。
            // ★ 「要不要先弹卡」的决策已经移到 BountyActivate：对方前场有怪时它压根不会弹，
            //   所以这里走到 = 千金大奖是干净的基底。以前这里还有一道「先弹再叠」的闸门，
            //   但那样弹完以后 _bountyActivated 就是 true，永远叠不上 —— 等于把斩杀线堵死。
            if (_bountyActivated) return false;

            // ★ v5.16：主阶 1 抢血时，对方站着 5300 以上的墙 → 800 的黑智天至爬上去也打不动，
            //   叠了只是把千金大奖 2800 换成 800 的纸糊怪。留给混沌大西洲 / 巨眼 / 托勒密那条解场线。
            if (Duel.Phase == DuelPhase.Main1
                && Enemy.GetMonsters().Any(c => c != null && c.IsFaceup() && c.Attack >= PhotonRoarBattle))
            {
                Log("黑智天至 → 对方有 " + PhotonRoarBattle + " 以上的墙，留在千金大奖");
                return false;
            }

            AI.SelectMaterials(new List<ClientCard> { baseXyz });
            Log("叠 黑智天至 ← " + baseXyz.Name);
            return true;
        }

        // 超银河眼光子龙-光子咆哮（R8，ATK 4500）
        //   召唤：「8星怪兽×3」，另外「1回合1次也能在自己场上的**8阶**超量怪兽上面重叠」
        //         —— 只能从黑智天至（R8）叠上来，千金大奖（R7）不行。
        //   ① 超量召唤成功 → 从卡组选 1 只「光子」怪兽守备特招或当素材（本卡组没有光子怪，空过）
        //   ② 拔 **3** 个素材：解放自己场上 1 只其他超量怪兽，这张卡以外场上全部表侧卡效果无效化到回合结束
        //
        // ★ v3.3：不再只在「刚好斩杀」时叠。4500（战阶 5300）是本卡组主要怪兽区唯一像样的打点。
        //   唯一不叠的情况：对方有一只连 5300 都顶不过去的怪 —— 那种场面交给
        //   混沌大西洲（≤3200 碾）/ 巨眼（抢）/ 托勒密（弹回卡组），叠上去也是撞墙。
        private bool PhotonRoarSummon()
        {
            if (Duel.Player != 0) return false;
            if (Duel.Phase != DuelPhase.Main1) return false;
            if (Duel.Turn == 1) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.PhotonRoar))) return false;

            ClientCard baseXyz = Bot.GetMonstersInMainZone()
                .FirstOrDefault(c => c.IsFaceup() && c.IsCode(CardId.Irisfil));
            if (baseXyz == null) return false;

            if (Enemy.GetMonsters().Any(c => c.IsFaceup() && c.Attack >= PhotonRoarBattle))
            {
                Log("光子咆哮 → 对方有 5300 以上的墙，先不叠");
                return false;
            }

            AI.SelectMaterials(new List<ClientCard> { baseXyz });
            Log("叠 光子咆哮 ← 黑智天至（战阶 5300）");
            return true;
        }

        // 银河眼反物质龙（R9，ATK 4000 / DEF 0）
        //   召唤：「9星怪兽×2」，另外「1回合1次也能在**持有超量素材 3 个以上**的自己的超量怪兽上面重叠」
        //         —— 所以只能从光子咆哮（4 素材）叠上来，千金大奖（2→剩 1）和黑智天至（3）都不够。
        //   ① 持有素材时，给对方的战斗伤害减半（打脸 4000 → 2000）
        //   ② **战斗阶段**（脚本 condition = aux.bpcon）拔 1 素材：
        //       这个回合同一次战斗阶段中最多可对怪兽攻击 2 次；
        //       拔掉的素材若为怪兽，可以再从卡组送 1 只同种族怪兽进墓 —— ★ 我们一律不送（见 OnSelectYesNo）
        //
        // ★ v3.4 用户口径：斩杀线是两条分支、同一个落点。
        //   B. 对方怪 ≥2 → **战前**就在光子咆哮上叠反物质龙，② 拔 1 打两次怪兽，把场面清掉。
        //   A. 对方怪 ≤1 → 光子咆哮先进战阶打 5300（反物质龙 ① 把战斗伤害减半，
        //                 4900 打脸只剩 2450，不能让它先上），
        //                 **主要阶段 2** 再补一层反物质龙 —— 这一层纯粹是给天霆号多攒 1 个素材
        //                 （光子咆哮 4 → 反物质龙 5 → 天霆号 6；千金大奖弹过卡就凑不到 3，直接跳过）。
        //   两条分支最后都在反物质龙上叠天霆号 → 下个对方回合拔 2 清场。
        private bool AntimatterSummon()
        {
            if (Duel.Player != 0) return false;
            if (Duel.Turn == 1) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Antimatter))) return false;

            // 基底只认光子咆哮：千金大奖 2 → 黑智天至 3 → 光子咆哮 4。
            // 反物质龙的叠放条件写死「持有超量素材 3 个以上」，凑不到就不出 ——
            // 千金大奖弹满卡之后光子咆哮可能只剩 2 个，这时硬叠是叠不上去的，
            // 直接停在光子咆哮打，M2 用现有的素材叠天霆号就好。
            ClientCard roar = Bot.GetMonstersInMainZone()
                .FirstOrDefault(c => c.IsFaceup() && c.IsCode(CardId.PhotonRoar));
            ClientCard baseXyz = roar != null && roar.HasXyzMaterial(3) ? roar : null;
            if (baseXyz == null)
            {
                if (roar != null)
                    Log("反物质龙 → 光子咆哮素材只有 " + roar.Overlays.Count + " 个，不够 3 个，不勉强出");
                return false;
            }

            int enemyMons = Enemy.GetMonsterCount();

            if (Duel.Phase == DuelPhase.Main1)
            {
                // B 线：对方 2 只以上 → 战前叠，战阶用 ② 一只一只啃
                if (enemyMons < 2) return false;
                AI.SelectMaterials(new List<ClientCard> { baseXyz });
                Log("叠 反物质龙 ← 光子咆哮（对方 " + enemyMons + " 只怪，战阶打两次）");
                return true;
            }

            if (Duel.Phase == DuelPhase.Main2)
            {
                // A 线：战阶已经打完，对方最多剩 1 只 → 补一层，给天霆号多 1 个素材
                if (enemyMons > 1) return false;
                AI.SelectMaterials(new List<ClientCard> { baseXyz });
                Log("叠 反物质龙 ← 光子咆哮（M2 补素材 → 天霆号）");
                return true;
            }

            return false;
        }

        // ===== v5.11：斩杀线新的一层 —— 银河眼重铠光子龙 =====
        //
        // 银河眼重铠光子龙 39030163（LIGHT 龙族 R8，4000/3500）
        //   召唤：「8星怪兽×3」，另外**也能在自己场上的「银河眼」超量怪兽上面重叠**来超量召唤
        //     （c39030163 的 ovfilter：`IsSetCard(0x107b)` + TYPE_XYZ；光子咆哮 setcode=0x55107b，正好命中）。
        //     叠放会继承基底的素材（procedure.lua 的叠放分支）→ 光子咆哮 4 → 重铠 5。
        //   ① 把这张卡的装备卡当素材（本卡组没有装备卡，等于白板，不发）
        //   ② 1回合1次，拔 1 素材，以**对方场上 1 张表侧表示的卡**为对象 → 破坏（IGNITION，自己主阶）
        //
        // ★ 用户 v5.11 口径：
        //   「刚刚那种情况其实可以在斩杀路线中通过在超银河眼光子龙上叠放重铠，重铠开效果破坏场地就行。」
        //   ⇒ 原来「专门拆对方场地/魔陷」是额外里塞一张征霸龙；现在改成把它做进斩杀线：
        //     光子咆哮（4500／战阶 5300）先打 → 叠重铠（4000／战阶 4800）→ ② 拆掉对方的场地/贴纸 → 再叠天霆号。
        //     一张卡同时干「打点」和「拆后场/场地」，额外就空出来还给漆梏之食兽。
        //
        // ★ 什么时候叠（两条分支）：
        //   M1：场上有值得拆的表侧卡（场地 / 永续 / 装备 / 高攻怪）且**本回合收不掉人头**时才叠 ——
        //       叠上去打点从 5300 掉到 4800，能斩杀就先打，别为了拆一张卡丢掉人头。
        //   M2：战阶已经打完，叠上去只有好处 —— ② 拆 1 张（素材 5 → 4），再叠天霆号还有 5 个素材，
        //       比直接「光子咆哮 → 天霆号」白赚一张破坏。
        private bool PhotonArmorSummon()
        {
            if (Duel.Player != 0) return false;
            if (Duel.Turn == 1) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.PhotonArmor))) return false;

            // 基底只认光子咆哮（本卡组唯一的「银河眼」超量）；没有它就没法叠。
            ClientCard baseXyz = Bot.GetMonstersInMainZone()
                .FirstOrDefault(c => c.IsFaceup() && c.IsCode(CardId.PhotonRoar));
            if (baseXyz == null) return false;

            // 没有值得拆的表侧卡就别叠：留在光子咆哮身上继续当 5300 打点。
            ClientCard prey = PhotonArmorPrey();
            if (prey == null) return false;

            if (Duel.Phase == DuelPhase.Main1)
            {
                // ★ v5.27 用户口径：对方场上有**威胁性的表侧卡**（有干扰效果的场地 / 永续魔陷、
                //   不会被战斗破坏的抗性怪）时，抢在战阶之前就叠重铠把它炸掉 ——
                //   「反正也不差那几百伤害」（光子咆哮 5300 → 重铠 4800）。
                //   replay 23:32 那局就是反例：战阶先打完，M2 才叠重铠拆对方的「剑斗兽的斗技场」，
                //   中间那段时间场地一直在给对方供资源 / 干扰。
                if (EnemyHasThreatFaceup())
                {
                    AI.SelectMaterials(new List<ClientCard> { baseXyz });
                    Log("叠 重铠光子龙 ← 光子咆哮（M1 抢拆：对方有威胁卡「" + prey.Name + "」，宁可少打 500 也先拆）");
                    return true;
                }

                // ★ v5.16 用户口径（默认档）：「等银河眼战斗完，主要阶段 2 再叠重铠和天霆号。」
                //   M1 叠上去打点从 5300 掉到 4800 —— 对方没有值得抢先处理的卡时，那 500 点比 M2 顺手拆更值。
                Log("重铠 → M1 不叠，等光子咆哮打完、M2 再拆「" + prey.Name + "」");
                return false;
            }

            AI.SelectMaterials(new List<ClientCard> { baseXyz });
            Log("叠 重铠光子龙 ← 光子咆哮（拔1 破坏对方「" + prey.Name + "」）");
            return true;
        }

        // 重铠 ②：拔 1 素材，破坏对方场上 1 张表侧卡。
        //   IGNITION → 只在自己回合的主阶；desc 用来和 ①（叠装备）分开。
        private bool PhotonArmorActivate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            if (EffectNegated()) return false;                 // 被无效还发 = 白扔 1 个素材
            if (!Card.HasXyzMaterial(1)) return false;
            if (ActivateDescription != DescPhotonArmorDestroy) return false;   // ① 叠装备：本卡组没有装备卡
            if (Duel.Player != 0) return false;                // IGNITION：只在自己回合

            ClientCard prey = PhotonArmorPrey();
            if (prey == null) return false;
            _armorTarget = prey;
            _armorUsedTurn = Duel.Turn;    // ★ v5.27：② 一回合一次 → 天霆号据此不再「等一个已经发不出来的效果」
            Log("重铠 ② 拔1 破坏 -> " + prey.Name + "（值 " + ArmorPreyValue(prey) + "）");
            return true;
        }

        // 值得拆的表侧卡：场地 100 ＞ 战破抗性怪 95 ＞ 永续 70 ＞ 装备 65 ＞ 其他表侧魔陷 60 ＞ 怪兽（按攻击力，≥2500 才算）。
        // 里侧盖卡不能选（脚本 desfilter = IsFaceup）。
        private ClientCard PhotonArmorPrey()
        {
            return Enemy.GetMonsters().Concat(Enemy.GetSpells())
                .Where(c => c != null && c.IsFaceup())
                .OrderByDescending(ArmorPreyValue)
                .FirstOrDefault(c => ArmorPreyValue(c) >= 60);
        }

        // ★ v5.27：对方场上有没有「值得抢在战阶前处理掉」的表侧卡？
        //   只认两类（用户口径：「有干扰效果的场地或者不会被战斗破坏的抗性怪」）：
        //     · 表侧场地魔法 —— 对方这卡组的资源/干扰引擎；
        //     · 表侧永续魔法 / 永续陷阱 —— 每回合都能动一次的贴纸；
        //     · 表侧怪兽且「不会被战斗破坏」—— 战阶根本啃不动，只能靠重铠 ② 效果拆。
        //   普通魔陷（一次性、且多半是盖着）和普通大怪都不算：前者 M2 顺手拆、后者战阶能战破。
        private bool EnemyHasThreatFaceup()
        {
            foreach (ClientCard c in Enemy.GetSpells())
            {
                if (c == null || !c.IsFaceup()) continue;
                if (c.Location == CardLocation.FieldZone || c.HasType(CardType.Field)) return true;
                if (c.HasType(CardType.Continuous)) return true;
            }
            foreach (ClientCard c in Enemy.GetMonsters())
            {
                if (c == null || !c.IsFaceup()) continue;
                if (RaiseMoonScriptWipe.CannotBeDestroyedByBattle(c.Id)) return true;
            }
            return false;
        }

        private int ArmorPreyValue(ClientCard c)
        {
            if (c == null || !c.IsFaceup()) return -1;
            if (c.Location == CardLocation.FieldZone || c.HasType(CardType.Field)) return 100;   // 场地：对方的资源引擎
            if (c.HasType(CardType.Monster) && RaiseMoonScriptWipe.CannotBeDestroyedByBattle(c.Id))
                return 95;                                                                      // 战破抗性怪：只能用效果拆
            if (c.HasType(CardType.Continuous)) return 70;
            if (c.HasType(CardType.Equip)) return 65;
            if (c.HasType(CardType.Spell) || c.HasType(CardType.Trap)) return 60;
            if (c.HasType(CardType.Monster)) return c.Attack >= 2500 ? 55 : 20;                 // 大怪才值得拆
            return 10;
        }

        // 天霆号 阿宙斯（R12，ATK 3000）
        //   召唤：「12星怪兽×2」，另外「在超量怪兽进行过战斗的回合有 1 次也能在自己场上的超量怪兽上面重叠」
        //   ① 自己·对方回合，拔 **2** 个素材 → 场上其他卡全部送去墓地
        //   ② 自己场上其他卡被战斗/对方效果破坏时，从手卡·卡组·额外卡组把 1 张卡当素材
        //
        // ★ 用户要求：光子咆哮打完战阶 → 主要阶段 2 叠天霆号，下个对方回合拔 2 清场。
        //   素材够不够？够 —— 见上面的说明，叠放会继承基底的全部素材（光子咆哮 4 → 天霆号 5）。
        private bool ZeusSummon()
        {
            if (Duel.Player != 0) return false;

            if (Duel.Phase == DuelPhase.Main2)
            {
                // ★ v5.16：重铠的 ② 还没用掉 → 先让重铠拆，别急着把它叠成天霆号。
                //   执行器列表里 SpSummon 段（第 6 段）在 Activate 段（第 8 段）**之前**，
                //   这里不让位的话同一个 idle cmd 就会直接「重铠 → 天霆号」，
                //   重铠 ② 永远没机会发，那 1 个素材和白赚的一张破坏全扔了。
                // ★ v5.27：加上「本回合还没用过 ②」—— `c39030163.lua` 的 e2 带 `SetCountLimit(1)`，
                //   拆完第一张后目标会换成第二张，原来那条判据会让天霆号一直等一个发不出来的效果，
                //   整回合叠不上去（replay 23:32:57 / 23:32:58 连打两次「等重铠」之后再没有「叠 天霆号」）。
                ClientCard armor = Bot.GetMonstersInMainZone()
                    .FirstOrDefault(c => c.IsFaceup() && c.IsCode(CardId.PhotonArmor));
                if (armor != null && armor.HasXyzMaterial(1) && _armorUsedTurn != Duel.Turn)
                {
                    ClientCard armorPrey = PhotonArmorPrey();
                    if (armorPrey != null)
                    {
                        Log("天霆号 → 等重铠 ② 先拆「" + armorPrey.Name + "」再叠");
                        return false;
                    }
                }

                // ★ v3.4 用户口径：两条分支最后都在反物质龙上叠天霆号，做下个对方回合的压制。
                //   所以这里不再要求「对方场上还有卡」—— 对方空场时，光子咆哮 / 反物质龙在对方回合
                //   什么都做不了，天霆号至少能拔 2 清场；打点从 5300 降到 4200（战阶）是换取主动权的代价。
                ClientCard baseM2 = BestZeusBase();
                if (baseM2 == null) return false;
                AI.SelectMaterials(new List<ClientCard> { baseM2 });
                Log("叠 天霆号 ← " + baseM2.Name + "（M2，下个对方回合拔2清场）");
                return true;
            }

            if (Duel.Phase != DuelPhase.Main1) return false;

            // ★ v5.27：M1 刚叠出来的重铠，② 还没发（SpSummon 段在 Activate 段之前）→ 先让它拆，别立刻叠走。
            ClientCard armorM1 = Bot.GetMonstersInMainZone()
                .FirstOrDefault(c => c.IsFaceup() && c.IsCode(CardId.PhotonArmor));
            if (armorM1 != null && armorM1.HasXyzMaterial(1) && _armorUsedTurn != Duel.Turn)
            {
                ClientCard preyM1 = PhotonArmorPrey();
                if (preyM1 != null)
                {
                    Log("天霆号 → M1 等重铠 ② 先拆「" + preyM1.Name + "」");
                    return false;
                }
            }

            if (!ShouldZeusOwnTurn()) return false;
            ClientCard baseXyz = BestZeusBase();
            if (baseXyz == null) return false;
            AI.SelectMaterials(new List<ClientCard> { baseXyz });
            Log("叠 天霆号 ← " + baseXyz.Name);
            return true;
        }

        // 叠天霆号的基底：★ 优先反物质龙（斩杀线的倒数第二层，M2 就是要叠在它身上），
        // 其次光子咆哮，再次黑智天至，最后任意有素材的超量怪。
        private ClientCard BestZeusBase()
        {
            return Bot.GetMonstersInMainZone()
                .Where(c => c.IsFaceup() && c.HasType(CardType.Xyz) && c.HasXyzMaterial(1)
                    && !c.IsCode(CardId.Zeus))
                // v5.11：重铠是斩杀线最后一层（素材最多、且 ② 已经用完），优先叠成天霆号；
                //   反物质龙不在卡组里了，但留着这一档以防换回来。
                .OrderByDescending(c => c.IsCode(CardId.PhotonArmor) ? 4
                    : c.IsCode(CardId.Antimatter) ? 3
                    : c.IsCode(CardId.PhotonRoar) ? 2 : (c.IsCode(CardId.Irisfil) ? 1 : 0))
                .FirstOrDefault();
        }

        // ★ v3.7 用户口径（大姐免疫）：
        //   大姐站 EX 区时带 EFFECT_IMMUNE_EFFECT（SetValue 1，条件 `GetSequence() > 4`），
        //   **不受任何其他卡的效果影响** → 天霆号清场根本碰不到她，她不算我方损失。
        //   所以「自己回合要不要清」只看两件事：
        //     · 对方这几张卡难不难解（清场是唯一手段 → 清）；
        //     · 我方场上除天霆号与免疫的大姐外还剩几张（要一起送墓 → 卡多就克制，留着对方回合再清）。
        private bool ShouldZeusOwnTurn()
        {
            // 解不掉的：贴纸封锁 / 无敌怪 / 攻击力压过我方最高打点的怪 → 战阶啃不动，直接清
            ClientCard problem = Util.GetProblematicEnemyCard();
            if (problem != null)
            {
                Log("天霆号 判断：对方有解不掉的「" + (problem.Name ?? "?") + "」→ 清");
                return true;
            }

            int hard = 0;
            if (Enemy.GetMonsterCount() >= 3) hard++;      // 对面铺开了，一发清 3 张以上最赚
            if (Enemy.GetSpellCount() >= 2) hard++;        // 后场 / 永续贴纸成型
            if (EnemyHasBiggerBeater()) hard++;            // 战阶啃不动

            int loss = MyBoardLoss();                      // 会被一起送墓的我方卡（已排除天霆号与免疫大姐）
            int need = loss >= 2 ? 2 : 1;                  // 自己场上卡多 → 对面得更硬才值得

            if (hard < need)
            {
                Log("天霆号 → 对方好处理（硬解点 " + hard + "/" + need + "），我方场上 " + loss + " 张，留着对方回合再清");
                return false;
            }
            Log("天霆号 判断：对方难解（硬解点 " + hard + "≥" + need + "，我方场上 " + loss + " 张）→ 清");
            return true;
        }

        // 对方回合（自由时点）→ 走同一张换账表。
        // ★ v5.8：原来是「对方 ≥2 张就清」，太松 —— 对方 2 张也要烧 2 个素材，还得把自己场上的卡一起送掉。
        private bool ShouldZeusOppTurn()
        {
            return ZeusExchangeWorth(false);
        }

        // ★ v5.8 用户口径（replay/优化9.yrp，13:20:10 那一手清场）：
        //   「这局天霆号在对方盖放了怪兽后发动效果清场是不对的 —— 这个场面明显我方优势大于对方，
        //     清场会把自己的卡送掉。天霆号在对方回合清场，更多时候是对方怪兽很多、或者对方盖放的卡不少的时候
        //     清收益才最大化；对方卡很少的时候清场，会让自己的卡送掉，得不偿失。
        //     另外还有种情况：天霆号在对方回合准备被解掉 / 被无效的时候才连锁清场 —— 这个也得看情况，
        //     如果对方第一个动作就是雷击之类的卡、而他场上没多少卡，你清场把自己的卡清掉也是血亏。」
        //
        //   ⇒ 所以「值不值得清」不看对方这一环是什么类型，只看这张换账：
        //       enemy = 对方场上会被一并清掉的卡（怪兽含盖放 + 魔陷含盖卡 + 场地/摇摆）
        //       loss  = 我方场上会被一起送墓的卡（天霆号自己、EX 区免疫的大姐不算）
        //       代价  = loss + 2 个超量素材（天霆号本来就要没时，这 2 个素材是沉没成本）
        //   两条门槛：
        //     · 天霆号能活：对方得**铺开**（≥3 张）而且算上 2 个素材仍然赚（enemy > loss + 1）；
        //       对方只有 1~2 张的小场面一律留着素材 —— 那不是清场，那是拿素材 + 自家场换对方的零星几张。
        //     · 天霆号要没（被取对象 / 被群体清场）：不亏就清（enemy > loss），不清白不清。
        //   ★ 素材折算：2 个素材按 1 张卡的价值算（enemy > loss + 1）。
        private bool ZeusExchangeWorth(bool doomed)
        {
            int raw = Enemy.GetMonsterCount() + Enemy.GetSpellCount();   // GetSpellCount 已含场地与摇摆区
            int loss = MyBoardLoss();
            // 「解不掉的卡」（贴纸 / 无敌怪 / 打点压过我方）实际价值超过 1 张，按 +1 溢价算
            int enemy = raw + (Util.GetProblematicEnemyCard() != null ? 1 : 0);

            bool worth = doomed ? enemy > loss : (enemy >= 3 && enemy > loss + 1);

            Log("天霆号 换账：对方 " + raw + " 张" + (enemy > raw ? "（+1 解不掉的卡）" : "")
                + "，我方会被一起拆 " + loss + " 张"
                + (doomed ? "（天霆号本来就要没 → 素材沉没）" : "（另付 2 素材）")
                + " → " + (worth ? "清" : "不清，留着素材"));
            return worth;
        }

        // 我方场上「清场真的会拆掉」的卡数：天霆号自己不算，站 EX 区的大姐（免疫）不算。
        private int MyBoardLoss()
        {
            int loss = 0;
            foreach (ClientCard c in Bot.GetMonsters())
                if (c != null && !c.IsCode(CardId.Zeus) && !JackpotImmune(c)) loss++;
            foreach (ClientCard c in Bot.GetSpells())
                if (c != null) loss++;
            return loss;
        }

        // 大姐只有站在额外怪兽区（Sequence ≥ 5）才吃 EFFECT_IMMUNE_EFFECT；在主区不免疫，不能当免死牌算。
        private static bool JackpotImmune(ClientCard c)
        {
            return c != null && c.IsCode(CardId.Jackpot) && c.Sequence >= 5;
        }

        // 对方有没有「我方战阶啃不动」的攻击表示怪兽
        private bool EnemyHasBiggerBeater()
        {
            int myBest = Bot.GetFaceupMonsters()
                .Where(c => c.Attack > 0).Select(c => c.Attack)
                .DefaultIfEmpty(0).Max();
            return Enemy.GetMonsters().Any(c => c.IsFaceup() && c.IsAttack() && c.Attack > myBest);
        }

        // ================= 陷阱 =================

        private bool ShouldSetSplendor()
        {
            // ★ v5.41 用户口径：水母适用 + 手牌有辉煌 → 直接盖放这张陷阱，其他动作一律不做，
            //   回合结束等对方动作。这里把「本回合走这条线」记下来 —— 辉煌一离手，只看手牌的
            //   `_jellyfishHandSplendor` 立刻就不成立了，不锁回合的话 bot 会接着铺场 / 叠超量。
            if (_jellyfishHandSplendor)
            {
                _setPassTurn = Duel.Turn;
                Log("水母 hold → 盖放辉煌（本回合只做这一手 + 群星①置顶），其余一律不动，等对方回合用辉煌拉大姐");
            }
            // 一律盖下去。手里留着是彻底的废牌（② 要求它在墓地），盖着既能当「大姐保险」，
            // 也能在对方回合接「丢手抽卡 → 千金大奖拔素材弹卡」那条连击。
            return true;
        }

        // 辉煌①：从额外特招 1 只本家超量，并把这张卡当素材。**是特招不是超量召唤**，
        // 所以千金大奖自己的 ② 不会因为这次出场而触发 —— 要靠「从手卡有怪兽特殊召唤」那一条。
        private bool SplendorActivate()
        {
            // 墓地 ②（QUICK_O 自由链）：除外自身抽 1。
            //   ★ 抽卡效果默认一律「另开连锁」（v5.14）；这张还是永久消耗（除外），
            //     更要等对方把场面摆出来、在自由时点用 C1 抽。
            if (Card != null && Card.Location == CardLocation.Grave)
            {
                // ★ v5.14 例外：墓地这张自己马上就要没 —— 最后机会，连锁抢开。
                //   「另开连锁」的前提是这张卡还在墓地；一旦被除外 / 被洗回卡组，
                //   ② 连发动权都没有，那个 C1 永远等不到。判据见 GraveSplendorDoomed()。
                //   这里不套 HoldDrawForOrder（副葬 / 群星置顶的顺序）：卡都快没了，
                //   先把它换成 1 张牌，顺序让位。
                if (GraveSplendorDoomed())
                {
                    _splendorGraveUsed = true;
                    Log("辉煌 ② 墓地这张将被除外/洗回 → 连锁抢开抽1");
                    return true;
                }

                // ★ v5.14 用户口径（replay _LastReplay，16:11~16:15）：**抽卡效果一律另开连锁**，
                //   自己回合也一样。v5.13 之前 `Duel.Player == 0` 分支排在 InChain 检查之前、
                //   直接返回 true，于是「大姐拔1抽1 / 帐① 自跳」之后它 C2 就跟上了
                //   （录像 16:14:07、16:14:52 两处都是这么连的）：
                //     · 同一时点两张抽卡挤进一条连锁，C1 结算后的自由时点被白白吃掉；
                //     · 连锁里抽到的本家接不上「被抽到时自跳」。
                if (InChain())
                {
                    Log("辉煌 ② 连锁中 → 不跟，等这条结算完再另开 C1");
                    return false;
                }

                if (Duel.Player == 0)
                {
                    // ★ v5.40：水母 combo 刚把一张本家压在牌堆顶 → 这一抽会把置顶那张挪回手里，
                    //   对方回合大姐抽 1 就抽不到它了（而本回合它又跳不了）→ 压住不抽。
                    if (JellyfishHoldingTop())
                    {
                        Log("辉煌 ② → 水母 combo 置顶的那张要留给对方回合大姐抽 1 → 这次不抽");
                        return false;
                    }
                    // ★ v5.2 顺序：手里还有副葬 / 群星① 还没置顶时，这一抽先压住。
                    //   副葬会洗牌，抽早了等于把「置顶 → 抽到」这条线打断。
                    //   （日志在 HoldDrawForOrder 里节流打一条，这里不再重复刷）
                    if (HoldDrawForOrder()) return false;
                    // ★ v5.32：场上那张辉煌还能先落千金 → 这一抽稍后（先抽就喂不到千金 ② 了）。
                    if (SplendorFieldCanLandBounty())
                    {
                        Log("辉煌 ② 先让场上的辉煌落千金 → 这一抽稍后再开");
                        return false;
                    }
                    _splendorGraveUsed = true;
                    Log("辉煌 ②自己回合抽1");
                    return true;
                }
                // ★ v5.9：和帐③ / 大姐拔1抽1 共用同一个「对方真的摆出来了」判据（EnemyDeployed）。
                //   对方空场时抽到的希耶娜/库露雪换不到东西，这一抽纯亏 —— 留着等它铺开。
                if (!EnemyDeployed())
                {
                    Log("辉煌 ② 对方还没铺开 → 留着");
                    return false;
                }
                _splendorGraveUsed = true;
                Log("辉煌 ②对方已铺开 → C1 抽1");
                return true;
            }

            if (Card == null || Card.Location != CardLocation.SpellZone) return false;

            // 这张盖卡自己会不会没：
            //   · 被对方取为对象（旋风 / 沙尘 / 双龙卷）→ Util.IsChainTarget；
            //   · 对方发动不取对象的群体清场且会拆到魔陷区（大岚 / 羽毛扫 / 天霆号）→ ChainHasZoneWipe。
            // ★ 只拆怪兽的（黑洞 / 雷击 / 激流葬 / 圣防）不算 —— 盖着的陷阱不在怪兽区。
            bool doomed = (!Card.IsFaceup() && Util.IsChainTarget(Card))
                || ChainHasZoneWipe(RaiseMoonScriptWipe.SpellTrap);

            // ★ v3.8 用户口径：① 结算到最后会把**墓地全部本家卡**洗回卡组
            //   （s.activate 里的 `s.tdfilter` = IsSetCard(0x1ec) + IsAbleToDeck），
            //   墓地那张辉煌会被一起洗走 → 它的 ②（除外自身抽 1）就白亏了。
            //   所以墓地还有辉煌没抽之前先让位：本次返回 false，让引擎继续遍历到墓地那张；
            //   它抽完会把自己除外（bfgcost），下一轮 idle cmd 场上这张再正常开，一张都不亏。
            //
            //   ★ 只在「墓地那张这次真的抽得成」时让位，否则会让防守落空：
            //     · `!_splendorGraveUsed` —— 本回合 ② 的名额还在（CountLimit 1 回合 1 次）；
            //     · `!InChain()` —— 不在连锁里。连锁中让位 = 这一环白白空过，
            //        对方回合该拉大姐防守的时候不能等；
            //     · 对方回合还要求 `EnemyHasBoard()`（墓地② 自己的条件，对方没铺开它也不会跟）。
            //   紧急情况（doomed，这张自己就要被拆了）一律不让位，先把自己结算掉。
            //     · 对方回合还要求 `EnemyDeployed()`（墓地② 自己的条件，对方没铺开它也不会跟）。
            //       v5.9：整行注释里的旧名 EnemyHasBoard 已合并进 EnemyDeployed。
            // ★ v5.32：combo 场景（自己回合 + 对方场上有卡 + 手上/场上有二速抽卡）→ 场上的辉煌
            //   **优先落地千金**，不再让位给墓地那张。否则「场上让给墓地、墓地让给场上」会互相让位，
            //   两张都不发，整条「先千金再抽卡」的链直接卡死。
            bool comboFirst = Duel.Player == 0 && EnemyFieldCount() > 0
                && (JackpotQuickDrawReady() || GraveSplendorDrawReady());
            bool graveCanDraw = !_splendorGraveUsed && !InChain() && !comboFirst
                && (Duel.Player == 0 || EnemyDeployed());
            if (!doomed && graveCanDraw && Bot.Graveyard.Any(c => c.IsCode(CardId.Splendor)))
            {
                Log("辉煌 ① → 先让墓地那张 ② 除外抽 1，再开场上这张（否则被洗回卡组白亏）");
                return false;
            }

            if (Duel.Player == 0)
            {
                // 自己回合：一回合只开一张（第二张留着下个对方回合当保险），主阶 1 用来抢血
                if (_splendorOwnUsed) return false;
                if (Duel.Phase != DuelPhase.Main1) return false;
                if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Bounty))) return false;
                if (Bot.GetMonstersInMainZone().Count >= 5) return false;

                // ★ v5.32 用户口径（replay _LastReplay 图2：后场辉煌 + EX 区大姐带素材，双方便宜）：
                //   「当升月有可以二速抽卡的效果（比如图中大姐或者墓地辉煌），后场有辉煌时，
                //     先发动辉煌拉千金大奖，然后再开二速抽卡效果，这样后续抽到的本家怪会跳下来
                //     触发千金大奖的效果弹卡（这个逻辑需要对方场上有怪兽或者魔法陷阱卡时才用）。」
                //   顺序能成立是因为注册顺序：Splendor(572) 在 Jackpot(575) 之前，
                //   同一个 idle cmd 里辉煌先被问 → 先落千金 → 下一轮才轮到拔 1 抽 1。
                //   千金 ② 的触发条件是「这张卡超量召唤的场合**或者从手卡有怪兽特殊召唤的场合**」，
                //   所以抽到的本家自跳正好喂给它 → 拔掉作为素材的这张辉煌 → 弹 1 张 → 辉煌进墓再抽 1。
                bool quickDraw = JackpotQuickDrawReady() || GraveSplendorDrawReady();
                if (quickDraw && EnemyFieldCount() > 0)
                {
                    _splendorOwnUsed = true;
                    _splendorTarget = CardId.Bounty;
                    _bountyFromSplendor = true;
                    Log("辉煌 ①自己回合 → 先落千金大奖（接二速抽卡抽到本家自跳 → 千金 ② 弹卡 → 辉煌② 再抽 1）");
                    return true;
                }

                _splendorOwnUsed = true;
                _splendorTarget = CardId.Bounty;
                _bountyFromSplendor = true;
                Log("辉煌 ①自己回合 → 千金大奖（抢血）");
                return true;
            }

            // ===== 对方回合 =====

            // ★ 「这张盖卡自己会不会没」的判定已提到函数开头（doomed），它优先级高于一切策略，
            //   也不受「一回合只开一张」限制（两张都盖着的话，被同一发清场一起拆掉，
            //   那就不如两张都换成怪）。
            if (doomed)
            {
                Log("辉煌 ① 自身将被拆除 → 连锁抢开");
                return SplendorEmergencySummon();
            }

            if (_splendorOppUsed) return false;

            bool jackpotAlive = Bot.GetMonstersInExtraZone().Any(c => c.IsCode(CardId.Jackpot));

            if (InChain())
            {
                // ★ v5.32 用户口径：「如果升月资源点只有一张辉煌（即前场连大姐都没有），
                //   那么则在对方发动检索效果时，二速连锁辉煌拉大姐达到置换检索效果。」
                //   连锁方向：C2 辉煌 ① 先结算 → 大姐落进 EX 区 → C1 那条检索处理时，
                //   大姐 ① 就能把「从卡组加入手卡」换成「自己抽 1」。
                //   这是唯一允许辉煌跟连锁的场合（其余仍是「不跟，防卡时点」）。
                if (!jackpotAlive && IsEnemySearchInChain()
                    && FreeEmzCount() > 0 && Bot.ExtraDeck.Any(c => c.IsCode(CardId.Jackpot)))
                {
                    _splendorOppUsed = true;
                    _splendorTarget = CardId.Jackpot;
                    Log("辉煌 ① 连锁对方检索 → 拉大姐进 EX 区（C1 处理时把精准检索换成自己抽 1）");
                    return true;
                }
                Log("辉煌 ① 对方连锁中 → 不跟（防卡时点）");
                return false;
            }

            if (!jackpotAlive)
            {
                // ① 大姐被打掉了 / 还没做出来 → 立刻把它拉回 EX 区（全抗 + 把对手检索换成自己抽 1）
                // ★ v5.32 用户口径：「后场辉煌也有不拉千金大奖的时候，比如对方场上打点很高，
                //   此时拉千金大奖没啥用，应该拉大姐让它被战斗破坏从而抽卡。」
                //   大姐 2100 被对方战破 → 大姐 ②「被对方送去墓地的场合，自己抽 1」
                //   —— 这是「拉大姐」在打点劣势局里的价值：不是站场，是换一张牌。
                if (FreeEmzCount() <= 0) return false;
                if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Jackpot))) return false;
                _splendorOppUsed = true;
                _splendorTarget = CardId.Jackpot;
                Log("辉煌 ①对方回合 → 大姐（守备表示，补 EX 区"
                    + (EnemyMaxAtk() >= 2800 ? "；对方打点已过 2800，被打掉也能抽 1" : "") + "）");
                return true;
            }

            // ② 大姐还站着 → 默认留着。只有「对面有卡必须解 + 手上接得上丢手抽卡」才开：
            //   辉煌拉千金大奖（守备）→ 丢手抽 1 → 抽到的本家怪兽自跳
            //   → 千金大奖 ②「从手卡有怪兽特殊召唤」拔掉作为素材的这张辉煌 → 弹对面卡
            //   → 辉煌在墓地 ② 除外抽 1。一张换「弹卡 + 抽 2 + 站一只 2800」。
            // ★ v5.32：对方最高打点已经能打穿千金的守备 2800 时，拉千金只是白送一只怪
            //   （对方战破后照样能继续打），不如留着这张辉煌等对面检索时置换。
            if (EnemyMaxAtk() >= 2800)
            {
                Log("辉煌 ① 对方最高打点 " + EnemyMaxAtk() + " ≥ 千金守备 2800 → 拉了白送，留着");
                return false;
            }
            if (EnemyFieldCount() == 0) return false;
            // 「抽 1」的来源有两条：手上的丢手抽卡件（朔 / 刃 / 灯），或者场上的二速抽卡
            // （EX 区大姐拔 1 / 墓地辉煌 ②）。★ v5.32：后者以前没算进来，于是「大姐带素材 + 后场辉煌」
            // 这种最标准的组合反而拉不出千金大奖。
            if (!HandHasDiscardDraw() && !JackpotQuickDrawReady() && !GraveSplendorDrawReady()) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Bounty))) return false;
            if (Bot.GetMonstersInMainZone().Count >= 5) return false;

            _splendorOppUsed = true;
            _splendorTarget = CardId.Bounty;
            _bountyFromSplendor = true;
            Log("辉煌 ①对方回合 → 千金大奖（守备，接二速抽卡 → 抽到的本家自跳 → 千金 ② 弹卡）");
            return true;
        }

        // ★ v5.14：墓地这张辉煌「这次不抽就没了」的两个来源。
        //   ② 的脚本是 `SetRange(LOCATION_GRAVE)` —— 一旦卡离开墓地，效果连发动权都没有，
        //   所以这两种情况必须在本条连锁里抢开（其余情况一律等自由时点另开 C1）：
        //     1) 对方取对象：墓穴的指名者 / D.D.乌鸦 / 深渊之兽 这类，结算时把墓地这张除外；
        //     2) 我方自己的「洗回」效果已经在链上：场上的辉煌 ①（结算时把墓地本家整批洗回卡组）、
        //        未眠之城 ③（结束阶段把墓地本家洗回卡组）。
        //   只判「墓地这张」；场上的辉煌走 ① 那条分支（doomed 判据在 SplendorActivate 里）。
        private bool GraveSplendorDoomed()
        {
            if (Card == null || Card.Location != CardLocation.Grave) return false;
            if (Util.IsChainTarget(Card)) return true;
            if (Duel.CurrentChain == null || Duel.CurrentChain.Count == 0) return false;
            foreach (ClientCard c in Duel.CurrentChain)
            {
                if (c == null || c.Controller != 0) continue;
                if (c.IsCode(CardId.Splendor) && c.Location == CardLocation.SpellZone) return true;
                if (c.IsCode(CardId.City) && Duel.Phase == DuelPhase.End) return true;
            }
            return false;
        }

        // 这张盖卡反正要没了 —— 把 ① 结算在清场 / 破坏之前：
        //   · 优先大姐（EX 区，全抗 + 把对手检索换成自己抽 1）；
        //   · EX 区满了 / 额外没有大姐，就落主要怪兽区的千金大奖（守备 2800）。
        // 无论拉哪只，这张辉煌都会变成它的超量素材 —— 也就是说它**自己躲过了那一发破坏**，
        // 之后还能靠「素材进墓 + 辉煌② 除外抽 1」把价值吃回来。
        private bool SplendorEmergencySummon()
        {
            if (FreeEmzCount() > 0 && Bot.ExtraDeck.Any(c => c.IsCode(CardId.Jackpot)))
            {
                _splendorOppUsed = true;
                _splendorTarget = CardId.Jackpot;
                Log("辉煌 ① 抢开 → 大姐（EX 区）");
                return true;
            }
            if (Bot.ExtraDeck.Any(c => c.IsCode(CardId.Bounty))
                && Bot.GetMonstersInMainZone().Count < 5)
            {
                _splendorOppUsed = true;
                _splendorTarget = CardId.Bounty;
                _bountyFromSplendor = true;
                Log("辉煌 ① 抢开 → 千金大奖（守备）");
                return true;
            }
            // 额外卡组没有能拉的目标 —— 引擎本来也不会给这个询问
            return false;
        }

        // ★ v5.32：场上的「二速抽卡」还开不开得出来。
        //   `JackpotQuickDrawReady` = EX 区站着一只能动的大姐、且还剩素材（拔 1 抽 1，QUICK_O，自己·对方回合都能开）；
        //   `GraveSplendorDrawReady` = 墓地还有辉煌、本回合 ② 的名额没用掉（除外自身抽 1）。
        //   这两个先落一个，才谈得上「先辉煌拉千金 → 再抽卡 → 抽到的本家自跳 → 千金 ② 弹卡」这条链。
        private bool JackpotQuickDrawReady()
        {
            return Bot.GetMonsters().Any(c => c != null && c.IsCode(CardId.Jackpot)
                && !c.IsDisabled() && c.HasXyzMaterial(1));
        }

        private bool GraveSplendorDrawReady()
        {
            return !_splendorGraveUsed && Bot.Graveyard.Any(c => c != null && c.IsCode(CardId.Splendor));
        }

        // 场上还盖着一张辉煌、而且它这一手就能把千金大奖摆出来（自己回合主阶 1、额外还有千金、主怪区有位）。
        // 用来让「墓地辉煌 ② 抽 1」先等一等 —— 先抽就变成「千金不在场时抽到本家」，自跳喂不到千金 ②。
        private bool SplendorFieldCanLandBounty()
        {
            if (_splendorOwnUsed) return false;
            if (Duel.Player != 0 || Duel.Phase != DuelPhase.Main1) return false;
            if (EnemyFieldCount() <= 0) return false;
            if (!Bot.ExtraDeck.Any(c => c.IsCode(CardId.Bounty))) return false;
            if (Bot.GetMonstersInMainZone().Count >= 5) return false;
            return Bot.SpellZone.Any(c => c != null && c.IsCode(CardId.Splendor) && !c.IsFaceup());
        }

        // 对方表侧怪兽里最高的攻击力（没有表侧怪 → 0）。用来判「千金守备 2800 会不会被一脚踹穿」。
        private int EnemyMaxAtk()
        {
            int best = 0;
            foreach (ClientCard c in Enemy.GetMonsters())
            {
                if (c != null && c.IsFaceup() && c.HasType(CardType.Monster))
                    best = Math.Max(best, c.Attack);
            }
            return best;
        }

        // 这一环是不是对方的「从卡组把卡加入手卡」效果 —— 大姐能替换的只有这一类。
        private bool IsEnemySearchInChain()
        {
            ClientCard last = Util.GetLastChainCard();
            return last != null && last.Controller == 1 && RaiseMoonScriptWipe.TakesFromDeckToHand(last.Id);
        }

        // ================= 额外怪兽效果 =================

        // 大姐：三条互相独立的效果挤在同一个「是否发动」通道里，靠 desc 分流。
        //   n=1（替换检索，EVENT_CHAIN_SOLVING 的替换询问）——纯赚，永远适用；
        //   n=2（拔 1 素材自己抽 1，QUICK_O 自由链）——**要挑时机**，见下；
        //   n=3（被送去墓地时抽 1，TRIGGER_O）——纯赚，永远适用。
        private bool JackpotActivate()
        {
            // ★ v5.3：被无效的大姐，三条效果（替换检索 / 拔1抽1 / 被送墓抽1）都只会白白被无效。
            if (EffectNegated()) return false;
            if (ActivateDescription != DescDrawByCost)
            {
                // ★ v5.25 用户口径（纠正 v5.21~v5.24 的整条思路）：
                //   「大姐的替换检索效果是**处理中再改效果**，不影响自己前面手坑的发动。」
                //   也就是说这一手和本连锁上已经交过什么手坑**互不干扰** —— 灰流丽该无效照样无效、
                //   该除外照样除外，大姐只是把那条效果在处理时换个操作（s.repop = 对方随机抽 1）。
                //   所以**不需要任何「本链已交手坑」的闸门**：引擎既然问了，发就完了。
                //   v5.21 加的 `NegateAlreadyInChain()` 闸门（连带 v5.24 换成 `ChainEffectNegatedByUs()`）
                //   都是在替规则做多余假设，反而在「链上有幽鬼兔」这类场合把对方的精准检索白放过去。
                //
                // ★ v5.26 用户口径（与上一条互补，**不是**把闸门加回来）：
                //   「如果对方要发动的检索效果**本身被无效**了，那么大姐的替换检索也无法生效了。」
                //   ——「有手坑」和「原效果被无效」是两件不同的事：
                //     · 幽鬼兔只是破坏那只怪 → 原检索照常结算 → **该替换**（v5.25 口径）；
                //     · 灰流丽 / 遮蒙者让原效果作废 → 替换**无从生效** → 不发（本条）。
                //   core 实证（processor.cpp 链结算 case 1 / case 2）：
                //     · 发动无效（CHAIN_DISABLE_ACTIVATE）→ 直接 EVENT_CHAIN_NEGATED、跳 step 9，
                //       **不触发** EVENT_CHAIN_SOLVING ⇒ 大姐压根不会被问，无需我方判断；
                //     · 效果无效（CHAIN_DISABLE_EFFECT）→ **仍触发** EVENT_CHAIN_SOLVING，大姐照样被问一次
                //       （日志实证 14:12:54.946 灰流丽 → 14:12:55.554 `大姐 发动 desc=1169449377`），
                //       随后在 case 2 因 `is_chain_disabled()` 跳过 operation ⇒ `s.repop`（抽 1）不会执行。
                //   ⇒ 这种情况答「是」不会让对方抽牌、也没有任何收益，只是多一次交互 + 一行容易误读的日志，
                //     所以在 C# 侧直接判掉，把「替换无从生效」这件事说清楚。
                if (ActivateDescription == DescNoMoreBet)
                {
                    if (ChainSearchNegatedByUs())
                    {
                        Log("大姐 替换检索 → 对方这条检索已被我方无效，替换无从生效 → 不发");
                        return false;
                    }
                    // ★ v5.36 用户口径：这条链上朔已经挂了「持续抽卡」（手牌很少才会走到那一步）——
                    //   大姐一替换，对方就不再有「从卡组加入手卡」，朔那一次的抽 1 也不会触发，
                    //   等于把刚装上的抽卡引擎掐掉。**两条路二选一**，手牌少时让给朔。
                    //   判据只认 `_squizContinuous`（它只在「链上最后一张是对方的检索」时才会置位），
                    //   不再重复查 `IsEnemySearchInChain()` —— 替换询问发生在链结算阶段，
                    //   那时 `Util.GetLastChainCard()` 未必还在，多查一条只会让闸门漏掉。
                    if (_squizContinuous)
                    {
                        Log("大姐 替换检索 → 朔已在链上挂好持续抽卡（手牌少），这条检索留给它抽1 → 不替换");
                        return false;
                    }
                    // ★ v5.29：替换的结果是 `Duel.Draw(tp,1)`（tp = 原链环持有者 = 对方），
                    //   它照样 raise `EVENT_TO_HAND`（上一位置 = 卡组）—— 锁鸟会因此跳出来，
                    //   但它接的是**我方大姐的结算**。置这个标记，让锁鸟这一手不接。
                    _jackpotReplacedSearch = true;
                    Log("大姐 替换检索（对方手牌 " + Enemy.Hand.Count + " 张 → 精准检索改成随机抽 1）");
                    return true;
                }
                Log("大姐 发动 desc=" + ActivateDescription);
                return true;
            }

            if (Card == null || !Card.HasXyzMaterial(1)) return false;

            // ★ 这只怪自己要被拆了（被取对象 / 群体清场）：素材留着也是跟着进墓，
            //   抢在离场前拔掉换 1 张牌，回来还能吃到「素材进墓」的收益。
            if (SelfDoomedOnField(RaiseMoonScriptWipe.Monster))
            {
                Log("大姐 自身将被拆除 → 抢开拔1抽1");
                return true;
            }

            // ★ v3.9 用户口径：**任何回合都不许跟着自己的效果连锁拔素材**。
            //   实证错误：C1 手牌斯奎兹「丢弃自身抽 1」→ AI 立刻 C2 连锁大姐拔素材抽 1。
            //   为什么错：
            //     · 连锁处理中抽到的卡，它「被抽到的那一刻」已经过去 —— 抽到的本家接不上
            //       「被抽到时自跳」，只能烂在手里；
            //     · 两张抽卡挤进同一条连锁，等于白白浪费掉 C1 结算完的那个自由时点。
            //   正确做法：等 C1 处理完（卡已经抽到手），再另开一条连锁拔素材抽。
            //   ★ 注意这条在「自己回合」分支之前 —— 之前只有对方回合查了 InChain，
            //     自己回合的 C2 连锁就是这么漏出去的。
            if (InChain())
            {
                Log("大姐 拔1抽1 · 连锁中 → 等这条结算完再另开（不 C2 跟自己的抽卡）");
                return false;
            }

            // 自己回合：随时拔。抽上来的本家「被抽到时自跳」是开新连锁的诱发效果，
            // 立刻就能跳下来接着做场，没什么好等的。
            if (Duel.Player == 0)
            {
                // ★ v5.40：水母 combo 置顶的那张要留到对方回合 → 自己回合这次拔素材抽 1 压住。
                if (JellyfishHoldingTop())
                {
                    Log("大姐 拔1抽1 → 水母 combo 置顶的那张要留给对方回合 → 这次不拔");
                    return false;
                }
                Log("大姐 拔1抽1（自己回合）");
                return true;
            }

            // 对方回合·自由时点：等对方真的把怪摆上来 / 贴了魔陷再抽，
            // 这样抽到的希耶娜能弹怪、库露雪能弹魔陷，落地就能解场。
            // ★ v5.9：判据从 `EnemyHasBoard`（连盖卡都算）收紧到 `EnemyDeployed`（怪登场 / 表侧永续·场地·灵摆）。
            //   用户口径：「对方空场你抽到希耶娜和库露雪换不到对方场上的卡 —— 血亏」，
            //   抽卡类效果统一按这一条判（帐③ / 大姐拔1抽1 / 墓地辉煌② 同一个闸门）。
            if (!EnemyDeployed())
            {
                Log("大姐 拔1抽1 · 对方还没铺开 → 留着");
                return false;
            }

            Log("大姐 拔1抽1（对方已铺开，C1）");
            return true;
        }

        // 千金大奖 ②：拔 N 个素材，把对方场上 N 张卡弹回卡组底（不取对象）。
        //
        // ★ 和斩杀线的互斥关系（机制硬约束，不是取舍）：
        //   黑智天至的 ovfilter 查的是「这个回合**没有在怪兽区域把效果发动**的超量怪」
        //   （lua 用全局 EVENT_CHAIN_SOLVED 打 flag）—— 千金大奖一旦发动②，
        //   本回合就再也叠不成黑智天至，整条 黑智天至 → 光子咆哮 → 反物质龙 直接断掉。
        //   所以决策只有一条分界：**对方前场挡不住、这一拳能收人头就留着当基底，否则弹**。
        //   对面站 2 只以上的怪时一律弹 —— 打掉一只的溢出伤害不穿透，弹回卡组才是真解。
        //
        // ★ v3.5 用户口径：该弹 2 就弹 2，不要吝啬素材。
        //   上一版为了保「千金大奖 1 → 黑智天至 2 → 光子咆哮 3 → 反物质龙 4」的素材账，
        //   硬锁 `SelectNumber(1)`，等于把一个「不取对象弹回卡组」的强解场打折用。
        //   现在改回弹满（min(自身素材数, 对方场上卡数)），素材不够导致反物质龙叠不出来，
        //   由 AntimatterSummon 自己跳过 —— 那是它的判断，不是弹卡要迁就的事。
        private bool BountyActivate()
        {
            if (Card == null || !Card.HasXyzMaterial(1)) return false;
            if (EffectNegated()) return false;

            int threat = Enemy.GetMonsters().Count + Enemy.GetSpells().Count;
            if (threat == 0) return false;

            // ★ v5.16 用户口径：抢血时千金大奖只当**斩杀链的基底**（R7 → 黑智天至 → 光子咆哮 5300）。
            //   黑智天至的 ovfilter 要求基底「这个回合没有在怪兽区域把效果发动」——
            //   这里弹一次卡就把 flag 打上，整条斩杀线直接断，只剩 2800 一拳。
            //   所以对方就算有后场也先不拆，交给 M2 的重铠 ② 顺手拆（那只不占打点）。
            // ★ v5.32：但「辉煌 ① 拉出来的千金」不是斩杀基底 —— 它本来就走
            //   「从手卡有怪兽特殊召唤 → 拔素材弹卡」这条路换资源，弹掉才是它的用途。
            if (RaceMode() && !_bountyFromSplendor)
            {
                Log("千金大奖 → 抢血模式，留着当斩杀基底（不弹卡，M2 用重铠拆）");
                return false;
            }

            // 只有「对方前场挡不住、而且这一拳能收」才留着当基底。
            // ★ 为什么要求对方怪兽 ≤1：打完一只怪多余的伤害**不会穿透**，
            //   对面站 2 只的话光子咆哮 5300 也只能打掉 1 只 —— 这种场面弹 2 张回卡组强得多。
            if (Enemy.GetMonsterCount() <= 1 && CanFinish())
            {
                Log("千金大奖 → 对方挡不住这一拳，留着当基底不弹");
                return false;
            }

            _bountyActivated = true;
            // cost = c:RemoveOverlayCard(tp,1,rt)，rt = 对方场上卡数；
            // 不显式指定就是**随机**拔（WindBot 的 OnAnnounceNumber 默认随机），必须自己算满。
            int n = Math.Min(threat, Math.Max(1, Card.Overlays.Count));
            AI.SelectNumber(n);
            // ★ v5.15 用户口径（优化8）：弹卡对象要挑「对方场上的高价值怪兽、或者盖放的后场」——
            //   v5.14 及以前这一步落到 OnSelectCard 的兜底 `cards.Take(want)`，等于按引擎给的顺序瞎弹，
            //   经常拿一只 1500 杂鱼换掉一次拔 2 素材。这里记下「还欠几个目标」，选卡由 BountyPreyValue 排序。
            _bountyBouncePending = n;
            Log("千金大奖 拔" + n + " 弹" + n + "（该弹几张弹几张）");
            return true;
        }

        // 千金大奖 ② 弹回卡组的「对方场上 1 张卡」有多值得弹（越高越先弹）。
        //   · 表侧怪兽：攻击力是它现在的实际压制力，额外卡组怪（终端）再加重；
        //   · 里侧盖卡（魔陷区）：信息未知，但魔陷区的盖卡在本卡组眼里一律按「干扰 / 召唤反应型」估值
        //     —— 用户口径明说「盖放的后场」是高优先级目标；
        //   · 表侧永续 / 场地 / 装备：已经生效，但通常不如一只 2500+ 的怪值钱；
        //   · 里侧盖怪：可能是展开点，排在表侧小怪之上、表侧大怪之下。
        private int BountyPreyValue(ClientCard c)
        {
            if (c == null) return -1;
            int v;
            if (c.Location == CardLocation.MonsterZone)
                v = c.IsFaceup() ? 2000 + c.Attack + (c.IsExtraCard() ? 800 : 0) : 2400;
            else if (!c.IsFaceup())
                v = 3000;   // 里侧后场：盖着的魔陷，价值最高的未知数
            else
                v = c.Location == CardLocation.FieldZone ? 2700 : 2600;
            if (c == Util.GetProblematicEnemyMonster()) v += 2000;
            return v;
        }

        // 超银河眼光子龙－光子咆哮（R8，ATK 4500）
        //   ① 超量召唤成功：从卡组把 1 只「光子」特招（守备）或当素材 —— **必须开**，这是展开的一环。
        //   ② 拔 **3** 个素材，再解放自己场上的 1 只超量怪 → 无效并破坏全场（含自己）的效果，
        //      持续到回合结束。代价太大（自己掉一只 Xyz + 3 素材 + 自己吃无效），用户口径「一般不用」。
        //   ★ 两个效果同一张卡、同一个 Activate 执行器，**不能靠 desc 区分**：
        //     ① 有 SetDescription(aux.Stringid(id,0))，② 脚本里**没有** SetDescription，
        //     desc 会撞成同一个值 —— 实机就是这么把 ② 放出去的。改用「询问时机」判：
        //     ① 是可选诱发 → 只会走 MSG_SELECT_EFFECTYN（Duel.SelectingEffectYn == true）；
        //     ② 是 QUICK_O 起动 → 只会出现在 idle / 连锁 / 战斗阶段询问里。
        private bool PhotonRoarActivate()
        {
            if (EffectNegated()) return false;
            if (!AI.SelectingEffectYn)
            {
                Log("光子咆哮 ②（拔3素材+解放自己的Xyz无效全场）→ 代价太大，不用");
                return false;
            }
            Log("光子咆哮 ①登场 → 从卡组拉「光子」");
            return true;
        }

        // 反物质龙 ②：**只能在战斗阶段发动**（脚本 condition = aux.bpcon）。
        //   拔 1 素材 → 这个回合「同一次战斗阶段中最多可对怪兽攻击 2 次」。
        //   ★ 判据是「打得过的怪有 2 只以上」，不是「对方有怪」：
        //     只有 1 只怪时打两次没意义，纯亏 1 个素材，还白送 ① 的减半给下一次打脸。
        private bool AntimatterActivate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            if (EffectNegated()) return false;
            if (!Bot.GetMonstersInMainZone().Contains(Card)) return false;
            if (!Card.HasXyzMaterial(1)) return false;
            // ② 的 condition 是 aux.bpcon = 只能在战斗阶段。整个 BP 的 phase 值落在
            // BattleStart(8) ~ Battle(0x80) 之间（含 BattleStep / Damage / DamageCal），Main2(0x100) 已经超出。
            if (Duel.Phase < DuelPhase.BattleStart || Duel.Phase > DuelPhase.Battle) return false;

            int beatable = Enemy.GetMonsters()
                .Count(c => !c.IsFaceup() || c.Attack <= Card.Attack);
            if (beatable < 2) return false;

            Log("反物质龙 ② 拔1 → 本回合可打怪兽两次（可打 " + beatable + " 只）");
            return true;
        }

        // 天霆号 ①：QUICK_O + EVENT_FREE_CHAIN，脚本里**没有 SetCountLimit**，
        //   cost 是硬拔 2 个素材（`RemoveOverlayCard(tp,2,2)`）→ 素材够就能在同一连锁上连开。
        //   AI 之前就是这么把自己拔空的：
        //     01:27:05 天霆号 ①清场 → 01:27:06 天霆号 ①清场（4 素材 → 0，对方回合一点压制都没有）
        //
        // ★ v3.6 用户口径：
        //   ① 自己回合：需要清掉对方场上的卡时才开，而且**只开一次**；
        //   ② 对方想无效它（链上最后一环是对方）→ 才补一手再清，这是唯一允许的「连锁自己」；
        //   ③ 其他任何情况都不许自己连锁自己；
        //   ④ 对方回合：对方登场怪兽 / 发动·贴永续魔陷时，连锁清场。
        //   核心目标：把 2~4 个素材留到对方回合做压制（每次清场固定烧 2 个）。
        private bool ZeusActivate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            // ★ v5.3：被无效了就别再拔 2 个素材（这是纯烧素材，一次 2 张）。
            if (EffectNegated()) return false;
            if (!Card.HasXyzMaterial(2)) return false;

            int myZeusInChain = 0;
            ClientCard last = null;
            if (Duel.CurrentChain != null)
            {
                foreach (ClientCard c in Duel.CurrentChain)
                {
                    if (c == null) continue;
                    if (c.IsCode(CardId.Zeus) && c.Controller == 0) myZeusInChain++;
                }
                last = Duel.CurrentChain.LastOrDefault();
            }

            // ---- A. 这条连锁上已经有我方天霆号的效果 ----
            if (myZeusInChain > 0)
            {
                // 已经补过一次 → 再跟就是把最后 2 个素材也烧掉，坚决停手
                if (myZeusInChain >= 2)
                {
                    Log("天霆号 → 本连锁已补过一次，不再拔素材");
                    return false;
                }
                // 只有「最后一环是对方的卡」才跟 —— 那是对方在无效 / 回应它。
                // ★ v5.8：补这一手同样是「再付 2 个素材换同一批卡」，边际上更要求对方真的铺开了，一样过换账表。
                if (last != null && last.Controller == 1)
                {
                    if (!ZeusExchangeWorth(false)) return false;
                    Log("天霆号 再连锁清场（对方要无效它）");
                    return true;
                }
                Log("天霆号 → 自己已在本连锁发过，不重复");
                return false;
            }

            // ---- B. 自己回合 ----
            if (Duel.Player == 0)
            {
                // ★ 主要阶段 2 不清：战阶已经过完，清场拿不到任何攻击收益；
                //   而天霆号在对方回合随时能清 —— 这时把素材整批留给对方回合，才是「留 2~4 素材压制」。
                //   日志实证：01:27:04 在 M2 叠出天霆号，01:27:05、01:27:06 连清两次，4 素材直接归零。
                if (Duel.Phase != DuelPhase.Main1)
                {
                    Log("天霆号 → M2 不清（战阶已过，素材留给对方回合）");
                    return false;
                }
                // 清场会把自己场上其他卡一起送墓 → 先算划不划算（大姐免疫，不算在里面）
                if (!ShouldZeusOwnTurn())
                {
                    return false;
                }
                Log("天霆号 ①清场（自己回合解场，剩 " + Math.Max(0, Card.Overlays.Count - 2) + " 素材留对方回合）");
                return true;
            }

            // ---- C. 对方回合 ----
            // C1. 对方在连锁上发动卡 / 贴永续 → 连锁清场（这是用户要的主场景）
            // ★ v5.8：这里原来只看「对方这一环是不是怪兽/永续/场地」，是就清 —— 于是对方丢一张手坑
            //   （优化9 的幽鬼兔）就能把我方整场 + 2 素材换成他对面的零星几张。
            //   现在必须先过换账表：类型只决定「要不要考虑跟」，划不划算由 ZeusExchangeWorth 判。
            //   天霆号自己这一环就要被拆（被取对象 / 群体清场）时，素材是沉没成本，类型门槛一并放开
            //   （雷击这类通常魔法也允许跟），但仍然要算账。
            if (last != null && last.Controller == 1)
            {
                bool doomed = SelfDoomedOnField(RaiseMoonScriptWipe.Monster);
                if (!WorthChainingZeus(last) && !doomed)
                {
                    Log("天霆号 → 对方发动的不是怪兽/永续魔陷，也没威胁到天霆号，不跟");
                    return false;
                }
                if (!ZeusExchangeWorth(doomed)) return false;
                Log("天霆号 ①清场（连锁对方「" + (last.Name ?? "?") + "」"
                    + (doomed ? "，天霆号自己将被拆" : "") + "）");
                return true;
            }
            // C2. 我方别的效果在链上 → 不跟着自己开（省素材，也防卡时点）
            if (last != null)
            {
                Log("天霆号 → 我方别的效果在连锁上，不跟");
                return false;
            }
            // C3. 自由时点（对方召唤成功 / 主阶开始）：对面铺开了才清
            if (!ShouldZeusOppTurn())
            {
                Log("天霆号 → 对方回合但换不起/对方还没铺开，留着素材");
                return false;
            }
            Log("天霆号 ①清场（对方回合，对方已铺开）");
            return true;
        }

        // 对方这一环值不值得用天霆号跟：怪兽（含场上的怪兽效果）、永续魔法·陷阱、场地。
        // ★ 不跟通常魔法 / 通常陷阱：那是一次性的交换，留着素材到后面更值。
        private bool WorthChainingZeus(ClientCard c)
        {
            if (c == null) return false;
            if (c.HasType(CardType.Monster)) return true;
            if (c.HasType(CardType.Continuous)) return true;
            if (c.HasType(CardType.Field)) return true;
            return false;
        }

        // ===== 针对性终端自己的效果 =====

        // 巨眼 ①：拔 1 抢怪。发动当回合这张卡不能攻击 → 只在自己回合主阶（战前）做，
        // 抢过来的怪当回合就能参与进攻（被抢的那只没有攻击限制）。
        private bool BigEyeActivate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            if (!Card.HasXyzMaterial(1)) return false;
            if (Duel.Player != 0) return false;
            ClientCard prey = StealTarget();
            if (prey == null) return false;
            _stealTarget = prey;
            Log("巨眼 ①夺取 " + prey.Name + " 的控制权");
            return true;
        }

        // 混沌大西洲 ①：把对方 1 只怪兽装备过来（+1000 攻）。
        // ★ 一回合只有 1 次，且发动过就没有伤害了 —— 自己回合解完场就收手，不重复开。
        private bool ChaosAtlandisActivate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            if (!Card.HasXyzMaterial(1)) return false;
            if (Duel.Player != 0) return false;
            ClientCard wall = EnemyWall();
            if (wall == null) return false;
            _chaosTarget = wall;
            Log("混沌大西洲 ①装备 " + wall.Name);
            return true;
        }

        // 托勒密 ②：拔 N 弹 N（回卡组）。对方回合也开 —— 这才是它站场的意义。
        // ★ 弹的是「怪兽」，所以以对方怪兽数为上限；一只都没有就不开（空拔纯浪费素材）。
        private bool OmegaActivate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            if (!Card.HasXyzMaterial(1)) return false;
            List<ClientCard> targets = Enemy.GetMonsters()
                .Where(c => !c.IsShouldNotBeTarget())
                .OrderByDescending(c => c.Attack)
                .Take(Card.Overlays.Count)
                .ToList();
            if (targets.Count == 0) return false;
            _omegaTargets = targets;
            AI.SelectNumber(targets.Count);
            Log("托勒密 ② 拔" + targets.Count + " 弹" + targets.Count + " 只怪兽回卡组");
            return true;
        }

        // No.76 ③（QUICK_O / 1 回合 1 次）：拔 1 素材，把对方墓地 1 只怪兽吸成自己的超量素材。
        // ★ v4.9：时点从「墓地里有怪就吸」改成「只在真有用的时候吸」。
        //   旧版只要对方墓地有怪就发 —— 于是对方回合刚开始、双方都还没动作时就把这次
        //   1 回合 1 次的二速机会烧掉，吸走一只对面根本没打算用的尸体。
        //   现在分两类时点：
        //     ① 立刻吸：对方墓地里有「在墓地就能主动发动效果」的怪（自除外 cost / 墓地起动 /
        //        墓地自复活）。这种怪留在墓地就是定时炸弹，拔走等于直接废掉它的墓地效果，
        //        而且对我们来说完全无害 —— 抢的就是它。
        //     ② 等窗口：对方真的开始动墓地时才吸
        //        a) 对方从墓地发动效果（LastChainLocation == Grave）；
        //        b) 对方发动的卡点名了自己墓地的怪兽（ChainTargets 里出现了对方墓地的怪）；
        //        c) 对方发动魔法 / 陷阱，且已经暴露过墓地利用（复活、回收多在魔法侧，
        //           而且不少要到处理时才挑目标，抓不到 ChainTargets）。
        //     ③ 其它时点一律不发，把这一次留到真正需要的时候。
        private bool Utopia76Activate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            if (EffectNegated()) return false;
            if (!Card.HasXyzMaterial(1)) return false;
            List<ClientCard> grave = Enemy.GetGraveyardMonsters();
            if (grave.Count == 0) return false;

            // ① 对方墓地有主动效果怪 → 立刻拔走那只（攻击力最高的优先，同分随意）
            ClientCard threat = grave.Where(HasGraveActiveEffect)
                .OrderByDescending(c => c.Attack)
                .FirstOrDefault();
            if (threat != null) return FireUtopia(threat, "对方墓地有主动效果怪");

            // ② 以下全是反应窗口：必须处在「对方刚发动卡」的连锁里
            bool inChain = Duel.CurrentChain != null && Duel.CurrentChain.Count > 0;
            if (!inChain || Duel.LastChainPlayer != 1) return false;
            ClientCard chainCard = Duel.CurrentChain[Duel.CurrentChain.Count - 1];

            // ②a 对方从墓地发动效果 → 它正在吃墓地，此刻抢最划算
            if (Duel.LastChainLocation == CardLocation.Grave)
            {
                ClientCard same = (chainCard == null) ? null
                    : grave.FirstOrDefault(c => c.Id == chainCard.Id);
                ClientCard pick = same ?? PickGraveStealTarget(grave);
                if (pick != null)
                    return FireUtopia(pick, "对方从墓地发动「" + (chainCard?.Name ?? "?") + "」");
            }

            // ②b 对方发动的卡以自己墓地的怪为对象（复活 / 回收点名）
            ClientCard targeted = Duel.ChainTargets == null ? null
                : Duel.ChainTargets.FirstOrDefault(c => c != null && c.Controller == 1
                    && c.Location == CardLocation.Grave && c.HasType(CardType.Monster));
            if (targeted != null)
            {
                ClientCard pick = grave.FirstOrDefault(c => c.Id == targeted.Id)
                    ?? PickGraveStealTarget(grave);
                if (pick != null)
                    return FireUtopia(pick, "对方以墓地「" + targeted.Name + "」为对象");
            }

            // ②c 对方发动魔法 / 陷阱 + 已暴露墓地利用（或墓地里躺着一只大怪）→ 按复活 / 回收处理
            if (chainCard != null && (chainCard.HasType(CardType.Spell) || chainCard.HasType(CardType.Trap)))
            {
                bool recycleRisk = _enemyGravePlay
                    || grave.Any(c => !HandTrapIds.Contains(c.Id) && c.Attack >= 2000);
                if (recycleRisk)
                {
                    ClientCard pick = PickGraveStealTarget(grave);
                    if (pick != null)
                        return FireUtopia(pick, "对方发动「" + chainCard.Name + "」（可能回收墓地）");
                }
            }

            // ③ 其它时点：留着这次机会
            return false;
        }

        // 决定发 No.76 之后统一从这里走：记下要点名的那只，紧接着的选卡询问直接照它选。
        private bool FireUtopia(ClientCard target, string reason)
        {
            _utopiaPending = true;
            _utopiaTarget = target;
            Log("No.76 ③ 吸对方墓地 → " + (target?.Name ?? "?") + "（" + reason + "）");
            return true;
        }

        // 挑「最值得拔走」的墓地怪兽：先排除手坑（墓地里的手坑基本是尸体，抢了没意义），再按攻击力。
        private static ClientCard PickGraveStealTarget(List<ClientCard> grave)
        {
            return grave.Where(c => c != null && !HandTrapIds.Contains(c.Id))
                    .OrderByDescending(c => c.Attack).FirstOrDefault()
                ?? grave.Where(c => c != null).OrderByDescending(c => c.Attack).FirstOrDefault();
        }

        // 这张卡是否「在墓地就能主动发动效果」（自除外 cost / 墓地起动 / 墓地自复活）。
        // 卡文走 NamedCard.Description —— WindBot 启动时从 cards.cdb 的 texts 表整库读入，
        // 所以不用维护卡号白名单，对面换什么卡组都能判。
        private static bool HasGraveActiveEffect(ClientCard c)
        {
            if (c == null || !c.HasType(CardType.Monster)) return false;
            string desc = c.Data?.Description;
            if (string.IsNullOrEmpty(desc)) return false;
            // 卡文里 ①②③ 之间带换行，先归一化，免得关键词被折行截断
            if (desc.Contains("\n") || desc.Contains("\r"))
                desc = desc.Replace("\r", "").Replace("\n", " ");
            if (desc.Contains("墓地的这张卡除外") || desc.Contains("把这张卡从墓地除外")
                || desc.Contains("从墓地除外才能发动")) return true;      // 自除外 cost 发动
            if (desc.Contains("这张卡在墓地存在的场合") && desc.Contains("才能发动")) return true;
            if (desc.Contains("把这张卡从墓地特殊召唤")) return true;      // 墓地自复活（僵尸带菌者一类）
            return false;
        }

        // 食兽：② 对方发动通常/速攻魔法 → 把那张卡吸成超量素材并免疫；
        //      ③ 结束阶段把素材里的魔法盖回来。两条都是纯赚，问就发。
        private bool ChersibActivate()
        {
            if (Card == null || Card.Location != CardLocation.MonsterZone) return false;
            Log("食兽 效果发动（吸魔法 / 结束阶段盖回）");
            return true;
        }

        // ================= 钩子 =================

        public override IList<ClientCard> OnSelectXyzMaterial(IList<ClientCard> cards, int min, int max)
        {
            // 升阶叠放：候选里会出现「场上的超量怪兽本体」，那才是要选的
            List<ClientCard> xyz = cards.Where(c => c.IsFaceup() && c.HasType(CardType.Xyz)).ToList();
            if (xyz.Count > 0 && min <= 1) return new List<ClientCard> { xyz[0] };

            List<ClientCard> lv7 = cards.Where(c => c.Level == 7).ToList();
            if (lv7.Count < min) return base.OnSelectXyzMaterial(cards, min, max);
            return PickMaterials(lv7, max);
        }

        // ★ 上级召唤的「解放选择」——这是上一版把两只 7 星献祭掉的真正原因。
        //   雅 ② 是「可以不用解放作召唤」，引擎发来的解放选择是 min=0 / max=2（可选 0~2 只），
        //   但 WindBot 的 GameAI.OnSelectSum 在拿不到合法结果时会走兜底：
        //       foreach (card in cards) list2.Add(card);   // 把候选全选了
        //   于是它一口气把场上怪兽全当解放献掉。这里直接回答「一个都不解放」。
        public override IList<ClientCard> OnSelectSum(IList<ClientCard> cards, IList<ClientCard> mandatoryCards,
            int sum, int min, int max, int hint, bool exactEqual)
        {
            // ★ v5.32：通召雅的询问期间，解放选择一律 0 只 —— 这是「场上 ≤2 只怪才敢通召」
            //   那条旧闸门的替代品。卡萝尔 ② 允许不解放作召唤，所以合法；标记消费一次即清。
            // ★ v5.33：标记无论 min 是多少都要消费掉（否则一次没命中的询问会把它留到下一条连锁里，
            //   把别的卡的解放选择误答成 0 只）。真正的献祭防护在 `SummonCarol` 的
            //   「我方场上没有任何怪兽」硬闸门（v5.44）—— min≠0 说明引擎已经决定要解放召唤，
            //   这里再答 0 只只会变成非法选择。
            if (_carolSummoning)
            {
                _carolSummoning = false;
                if (min == 0)
                {
                    Log("解放选择：0 只（卡萝尔 ② 不解放）");
                    return new List<ClientCard>();
                }
                Log("解放选择：引擎要求解放 " + min + " 只 → 这手不该发生（SummonCarol 的闸门漏了），照 base 处理");
            }
            if (min == 0 && (mandatoryCards == null || mandatoryCards.Count == 0) && hint != 500 && hint != 512 && sum <= 0)
            {
                Log("解放选择：0 只（不解放）");
                return new List<ClientCard>();
            }
            return base.OnSelectSum(cards, mandatoryCards, sum, min, max, hint, exactEqual);
        }

        // 两只 R7 的表示形式：
        //   · 自己回合（先攻首回合除外）→ 攻击表示，要抢血；
        //   · 对方回合 / 先攻首回合 → 守备表示，千金大奖 2800、大姐 2100，对方得先把屁股打穿。
        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)
        {
            // ★ v5.35 用户口径（施工单第 70 节）：雅·卡萝尔「不解放召唤」落地面板是 700，不是卡面 1400。
            //   c11333460.lua 的 ntop 里挂了 EFFECT_SET_BASE_ATTACK(700)（RESET_EVENT+0xff0000，
            //   在场期间一直 700）。下面所有「能不能站攻表 / 能不能换掉对面谁」的估算一律用 700，
            //   否则对着 1000 出头的怪会把 700 的她摆上去白送。标记取用即清，只在这次召唤问位时有效。
            bool carolAtk700 = _carolNoTribute;
            if (cardId == CardId.Carol) _carolNoTribute = false;

            // 黑智天至 800/1250 —— 只是升阶的中转站，没叠上去就是个纸糊的，一律守备。
            // 天霆号 3000/3000 —— 主要阶段 2 出、要在对方回合拔 2 清场，守备更耐。
            if (cardId == CardId.Irisfil || cardId == CardId.Zeus)
            {
                if (positions.Contains(CardPosition.FaceUpDefence))
                {
                    Log((cardId == CardId.Zeus ? "天霆号" : "黑智天至") + " 守备表示");
                    return CardPosition.FaceUpDefence;
                }
            }
            // 光子咆哮 4500（战阶 5300）/ 反物质龙 4000（战阶 4900）：主要怪兽区的打点，站着就是要打人（守备 0 一碰就碎）
            if ((cardId == CardId.PhotonRoar || cardId == CardId.Antimatter)
                && positions.Contains(CardPosition.FaceUpAttack))
            {
                Log((cardId == CardId.Antimatter ? "反物质龙" : "光子咆哮") + " 攻击表示");
                return CardPosition.FaceUpAttack;
            }

            // ★ v5.16 抢血模式（用户口径：「升月本家的怪兽都应该是攻击表示特殊召唤」）：
            //   对方前场空 / 只有 1 只软怪 + 我方是后攻或对方回合数已过 → 这一回合的目标就是打脸，
            //   不需要先找到「打得死的怪」才肯翻过来。原来 v5.15 的守备规则只看 CanFinish / FindTradeTarget，
            //   于是「对手空场、我该抢 5600 血」这种局面被整回合守掉了（录像 21:27 三连守备）。
            //   例外：黑智天至（800）与天霆号（3000）仍守备 —— 它们在上面已经提前返回，
            //   而且黑智天至出了主阶就会立刻叠成光子咆哮，站着挨打没意义。
            if (RaceMode() && positions.Contains(CardPosition.FaceUpAttack))
            {
                string nm = null;
                if (cardId == CardId.Hiena) nm = "希耶娜";
                else if (cardId == CardId.Kurusu) nm = "库露雪";
                else if (cardId == CardId.Siero) nm = "帐·西艾萝";
                else if (cardId == CardId.Carol) nm = "雅·卡萝尔";
                else if (cardId == CardId.Rabi) nm = "翼·菈碧";
                else if (cardId == CardId.Squiz) nm = "朔·斯奎兹";
                else if (cardId == CardId.Bounty) nm = "千金大奖";
                else if (cardId == CardId.Jackpot) nm = "大姐";
                if (nm != null)
                {
                    Log("抢血模式 → " + nm + " 攻击表示（对方前场压不住，这回合要打脸）");
                    return CardPosition.FaceUpAttack;
                }
            }

            // ★ v5.30 用户口径（replay/_LastReplay.yrp + 截图：对方空场，AI 把西艾萝 2100 守备摆下来）：
            //   「对方空场，本家怪完全可以攻击表示特殊召唤，就算攻击力低，后续也可以用来叠放超量，
            //     不用担心会被对面打死。」
            //   · 判据用 SafeToAttack(打点)：对方前场空 / 表侧怪全打不过我 → 攻表零代价；
            //   · 覆盖本家六只下级（希耶娜 / 库露雪 / 西艾萝 / 卡萝尔 / 菈碧 / 斯奎兹）；
            //     千金大奖（1400）与大姐（2100）走下面各自那条，别在这里抢；
            //   · 只在**我方回合且不是先攻首回合**成立：对方回合我方特召（菈碧 ③ 加速超量）落地就要挨打，
            //     先攻 Turn 1 本来不能攻击 —— 这两处仍然守备，别把屁股白送；
            //   · 超量不看表示形式（c47828069.lua 的 xyzcon 只查 LOCATION_MZONE），
            //     攻表落地既不影响叠 R7，还让这回合的战阶有东西可打（守备落地会拖累 GoToBattle）。
            if (Duel.Player == 0 && Duel.Turn > 1 && positions.Contains(CardPosition.FaceUpAttack))
            {
                int bodyAtk = cardId == CardId.Hiena || cardId == CardId.Kurusu ? 2800
                    : cardId == CardId.Siero ? 2100
                    : cardId == CardId.Carol ? (carolAtk700 ? 700 : 1400)   // ★ v5.35：不解放召唤 = 700
                    : (cardId == CardId.Rabi || cardId == CardId.Squiz) ? 700 : -1;
                if (bodyAtk > 0 && SafeToAttack(bodyAtk))
                {
                    string bodyName = cardId == CardId.Hiena ? "希耶娜"
                        : cardId == CardId.Kurusu ? "库露雪"
                        : cardId == CardId.Siero ? "帐·西艾萝"
                        : cardId == CardId.Carol ? "雅·卡萝尔"
                        : cardId == CardId.Rabi ? "翼·菈碧" : "朔·斯奎兹";
                    Log("对方前场压不住 → " + bodyName + " 攻击表示特召（按 " + bodyAtk
                        + " 估打点；超量不看表示形式，攻表还能当战阶的打点）");
                    return CardPosition.FaceUpAttack;
                }
            }

            // 针对性 R7（巨眼 2600 / 混沌大西洲 3300 / 托勒密 2700 / No.76 2700 / 食兽 2100）：
            // 只有「自己回合主阶 1 且打得过」才攻击抢血；
            // 主要阶段 2 出的托勒密一律守备 —— 它是要活到对方回合去弹卡的，2700 的屁股比 2700 的打点值钱。
            if (cardId == CardId.BigEye || cardId == CardId.ChaosAtlandis || cardId == CardId.Omega7
                || cardId == CardId.Utopia76 || cardId == CardId.Chersib)
            {
                int atk = cardId == CardId.BigEye ? 2600
                    : cardId == CardId.ChaosAtlandis ? 3300
                    : cardId == CardId.Chersib ? 2100
                    : 2700;
                bool attack = Duel.Player == 0 && Duel.Phase == DuelPhase.Main1 && SafeToAttack(atk);
                CardPosition want = attack ? CardPosition.FaceUpAttack : CardPosition.FaceUpDefence;
                if (positions.Contains(want))
                {
                    Log("<针对性R7> " + (attack ? "攻击表示" : "守备表示"));
                    return want;
                }
            }

            if (cardId == CardId.Bounty || cardId == CardId.Jackpot)
            {
                int atk = cardId == CardId.Jackpot ? 2100 : 1400;
                bool wantAttack = Duel.Player == 0 && Duel.Turn > 1 && SafeToAttack(atk);
                CardPosition pick = wantAttack ? CardPosition.FaceUpAttack : CardPosition.FaceUpDefence;
                if (positions.Contains(pick))
                {
                    Log((cardId == CardId.Bounty ? "千金大奖" : "大姐") + (wantAttack ? " 攻击表示" : " 守备表示"));
                    return pick;
                }
            }
            // ★ v4.6 用户要求：本家下级里「攻击力低、守备力高」的，登场时守备表示更好。
            //   本家 Lv7 的攻守（cards.cdb）：希耶娜 2800/1400、库露雪 2800/700、西艾罗 2100/2100、
            //   卡萝尔 1400/1400、**翼·菈碧 700/2100**、**朔·斯奎兹 700/1400**。
            //   只有菈碧 / 斯奎兹是 ATK < DEF，其余都是攻 ≥ 守，不适用。
            //   脚本特招用的是 POS_FACEUP（攻 / 守二选一）→ 引擎会问 MSG_SELECT_POSITION，这里的选择真生效。
            //     · 先攻首回合（Duel.Turn==1 我方回合）必守备：这回合本来就不能攻击，
            //       2100 的屁股远好过 700 的攻击表示 —— 攻表示等于白送对方一个战破靶 + 战斗伤害；
            //     · 其它回合同样守备：700 打点打不出有效伤害，躺着更耐打；
            //     · 唯一例外：对方 LP 已被压到 ≤ 700 且对方前场空（这一拳就能收）→ 保持攻击表示。
            //   守备不影响当 R7 超量素材，也不影响菈碧 ③「对方回合加速超量」
            //   （c47828069.lua 的 xyzcon 只查 LOCATION_MZONE，不看表示形式）。
            // ★ v5.12 用户口径（replay/优化12 截图：雅 1400/1400 攻击表示站着）：
            //   「卡萝尔从手牌特殊召唤的时候最好守备表示，除非这回合你要用它来进行战斗。」
            //   雅是 1400/1400，攻守同值 —— 站着只有 1400 打点（打谁都不够），躺着还能挡刀、照样当 R7 素材。
            //   「这回合要用它战斗」只有两种情况：
            //     · 本回合能收人头（她的 1400 也会算进伤害）；
            //     · 场上正好有一只她打得死、而且值得杀的目标（FindTradeTarget）。
            //   ★ 落地守备不会漏掉后面的战破机会 —— 主阶里 ReposTradeUp（v5.4）还会把她翻过来。
            //   （v5.5 那条「只在后攻首回合守备」的旧规则被这条覆盖：现在任何回合特招都先守备。）
            // ★ v5.15 用户口径（优化6）：「升月抽卡抽到的本家怪兽，除了库露雪和希耶娜**一律守备表示**特招；
            //   下个自己回合要不要转攻看场面（ReposTradeUp / ReposToAttack 会处理），
            //   只有这回合真要打伤害（能收人头 / 打得死场上某只）才攻表。」
            //   · 希耶娜 / 库露雪 2800 打点：站着既能压制又能反打，例外攻表；
            //   · 帐 2100/2100、雅 1400/1400 攻守同值：躺着不亏打点、还能挡刀，
            //     且守备**不影响**帐 ③ 抽 1（脚本只查 LOCATION_MZONE）、也不影响当 R7 素材。
            if ((cardId == CardId.Hiena || cardId == CardId.Kurusu)
                && positions.Contains(CardPosition.FaceUpAttack))
            {
                Log((cardId == CardId.Hiena ? "希耶娜" : "库露雪") + " 特招 → 攻击表示（2800 打点，压制/反打）");
                return CardPosition.FaceUpAttack;
            }
            if (cardId == CardId.Siero && positions.Contains(CardPosition.FaceUpDefence))
            {
                bool willBattle = Duel.Player == 0 && Duel.Turn > 1 && Duel.Phase == DuelPhase.Main1
                    && (CanFinish() || FindTradeTarget(2100) != null);
                if (!willBattle)
                {
                    Log("帐·西艾萝 特招 → 守备表示（2100/2100，这回合不用它打）");
                    return CardPosition.FaceUpDefence;
                }
            }


            // ★ v5.12：雅·卡萝尔（卡面 1400/1400）—— 同上，默认守备，只有这回合要用她打才攻表。
            //   v5.35：不解放召唤时她的基础攻击力是 700（守备仍是 1400），换怪能力按 700 算。
            if (cardId == CardId.Carol && positions.Contains(CardPosition.FaceUpDefence))
            {
                int carolAtk = carolAtk700 ? 700 : 1400;
                bool willBattle = Duel.Player == 0 && Duel.Turn > 1 && Duel.Phase == DuelPhase.Main1
                    && (CanFinish() || FindTradeTarget(carolAtk) != null);
                if (!willBattle)
                {
                    Log("雅·卡萝尔 特招 → 守备表示（" + carolAtk + "/1400，这回合不用她打）");
                    return CardPosition.FaceUpDefence;
                }
            }

            if ((cardId == CardId.Rabi || cardId == CardId.Squiz)
                && positions.Contains(CardPosition.FaceUpDefence))
            {
                bool canFinish = Enemy.LifePoints <= 700
                    && !Enemy.GetMonsters().Any(c => c != null && c.IsFaceup());
                // ★ v5.4 用户口径：对方场上有「打点压得住、但本身价值很高」的怪时，
                //   700 的小怪该攻出去把它换掉，而不是躺着。见 FindTradeTarget 的说明。
                // ★ v5.5：后攻首回合（Turn 1 对方回合）例外 —— 用户要求菈碧「直接守备表示特殊召唤」，
                //   这一回合它是凑 R7 的占位料。真要打，轮到自己的主阶还有 ReposTradeUp 会把它翻过来，
                //   所以落地时统一守备并不会漏掉 v5.4 的战破机会。
                ClientCard trade = (Duel.Player == 0 && Duel.Turn > 1 && Duel.Phase == DuelPhase.Main1
                    && positions.Contains(CardPosition.FaceUpAttack))
                    ? FindTradeTarget(700) : null;
                if (trade != null)
                {
                    Log((cardId == CardId.Rabi ? "翼·菈碧" : "朔·斯奎兹") + " 攻击表示 → 战破对方高价值「"
                        + trade.Name + "」（一换一，比横着挨打划算）");
                    return CardPosition.FaceUpAttack;
                }
                if (!canFinish)
                {
                    Log((cardId == CardId.Rabi ? "翼·菈碧" : "朔·斯奎兹") + " 低攻高守 → 守备表示"
                        + (Duel.Turn == 1 && Duel.Player == 0 ? "（先攻不能攻击，留屁股）" : ""));
                    return CardPosition.FaceUpDefence;
                }
            }

            return base.OnSelectPosition(cardId, positions);
        }

        // ★ v5.30 用户口径：「对方空场，本家怪完全可以攻击表示，就算攻击力低也不怕」。
        //   守备的 700 / 1400 / 2100 在空场面前等于白躺 —— 更要命的是 GoToBattle 要求
        //   「场上有攻击表示的怪」才肯进战阶（见第 10 段注释），全场守备 = 整回合 0 伤害。
        //   只在对方前场**彻底空着**时翻：有里侧怪就交给 ReposTradeUp / 战阶的常规判断，
        //   别在这里替玩家猜里侧是不是反转怪兽。
        private bool ReposPressEmptyField()
        {
            if (Duel.Player != 0 || Duel.Turn == 1) return false;
            if (Duel.Phase != DuelPhase.Main1) return false;
            if (Card == null || !Card.IsFaceup() || !Card.IsDefense()) return false;
            if (Card.Attack <= 0) return false;
            if (!(Card.IsCode(CardId.Hiena) || Card.IsCode(CardId.Kurusu) || Card.IsCode(CardId.Siero)
                || Card.IsCode(CardId.Carol) || Card.IsCode(CardId.Rabi) || Card.IsCode(CardId.Squiz)))
                return false;
            if (Enemy.GetMonsters().Count != 0) return false;
            Log(Card.Name + " 转攻击 → 对方空场，这回合直接打脸（表示形式不影响后续叠 R7）");
            return true;
        }

        // 自己回合把守备的 R7 翻成攻击：只在打得过的时候翻，否则留着 2800 的屁股
        private bool ReposToAttack()
        {
            if (Duel.Player != 0) return false;
            if (Duel.Phase != DuelPhase.Main1) return false;
            if (!Card.IsDefense()) return false;
            if (!SafeToAttack(Card.Attack)) return false;
            Log(Card.Name + " 转攻击");
            return true;
        }

        // ★ v4.6：低攻高守的本家下级（翼·菈碧 700/2100、朔·斯奎兹 700/1400）。
        //   OnSelectPosition 已经会在登场那一刻直接选守备；这条是兜底 ——
        //   万一某条特招路径把它按攻击表示摆下来，轮到自己的主阶就翻成守备。
        //   700 的攻击力打不出有效伤害，2100 的守备力才是它真正的价值（先攻期尤其明显）。
        private bool ReposLowAtkToDefence()
        {
            if (Duel.Player != 0 || Card == null) return false;
            if (!Card.IsFaceup()) return false;
            // ★ v5.4：对方有值得战破的高价值怪时不翻守备 —— 否则会和 ReposTradeUp 打架
            //   （这条翻守备、那条翻攻击，一轮来回翻，主阶白耗）。
            if (FindTradeTarget(Card.Attack) != null)
            {
                if (Card.IsAttack()) Log(Card.Name + " 保持攻击表示（场上有值得换掉的高价值怪）");
                return false;
            }
            if (!Card.IsAttack()) return false;
            // ★ v5.30：对方前场压不住（空场 / 表侧怪全打不过我）时**不翻守备** ——
            //   攻表的 700 也能直接打脸，而且翻守备会让 GoToBattle 失去「有攻击表示的怪」这个前提，
            //   整回合不进战阶。真被打死也无所谓（用户口径：「不用担心会被对面打死」）。
            if (SafeToAttack(Card.Attack))
            {
                Log(Card.Name + " 保持攻击表示（对方前场压不住 → 攻表能直接打脸，不翻守备）");
                return false;
            }
            if (Card.Attack >= Card.Defense) return false;
            if (Enemy.LifePoints <= Card.Attack) return false;   // 这一拳就能收掉 → 保持攻击表示
            Log(Card.Name + " 低攻高守 → 翻守备");
            return true;
        }

        // ★ v5.4 用户要求：不只翼·菈碧 —— 我方任何「低攻」怪只要打得死对方一只**高价值**怪兽，
        //   就该翻攻击去战破它。理由：700 的怪横着本来也拦不住什么，战破救援猫（300/100，解放自己
        //   从卡组特招 2 只兽族）这种「卡组引擎」等于用一只废怪换掉对方半套展开；
        //   哪怕它下回合被对方战破，这笔交换也是赚的。
        //   实证：replay/优化7.yrp —— 我方 700 的翼·菈碧守备横了整个自己回合，300 的救援猫安稳站着。
        //   限制：只处理 ATK < 2000 的小怪（大怪的翻攻交给 ReposToAttack / ReposZeus 的 SafeToAttack 口径，
        //        它们要防的是「翻攻后被对方 3000 大打脸」），且必须真能战破目标。
        private bool ReposTradeUp()
        {
            if (Duel.Player != 0 || Card == null) return false;
            if (Duel.Phase != DuelPhase.Main1) return false;
            if (!Card.IsFaceup() || !Card.IsDefense()) return false;
            if (Card.Attack >= 2000) return false;                       // 大怪走各自的 SafeToAttack 判断
            ClientCard target = FindTradeTarget(Card.Attack);
            if (target == null) return false;
            int power = target.IsDefense() ? target.Defense : target.Attack;
            Log(Card.Name + " 转攻击 → 战破对方高价值「" + target.Name + "」（" + Card.Attack + " > " + power
                + (IsResourceMonster(target) ? "，卡组资源引擎" : "，留着下回合更麻烦") + "）");
            return true;
        }

        // 对方场上的怪里，挑一只「打得死 + 值得打」的；打不掉或没价值就返回 null。
        // 价值判据（IsWorthKilling）宁可偏保守，别让 700 的小怪去换一只无所谓的杂鱼。
        private ClientCard FindTradeTarget(int myAtk)
        {
            if (myAtk <= 0) return null;
            return Enemy.GetMonsters()
                .Where(c => c != null && c.IsFaceup() && c.HasType(CardType.Monster)
                    && CanBeatBy(myAtk, c) && IsWorthKilling(c))
                .OrderByDescending(TradeValue)
                .FirstOrDefault();
        }

        private static bool CanBeatBy(int myAtk, ClientCard target)
        {
            if (target == null || !target.IsFaceup()) return false;
            int power = target.IsDefense() ? target.Defense : target.Attack;
            return myAtk > power;
        }

        // 这只怪「值不值得用我方一只小怪去换」：
        //   ① 卡组资源引擎（救援猫这类「解放/送墓自己 → 从卡组特招/检索/堆墓」）；
        //   ② 对手花额外卡组资源做出来的怪；
        //   ③ 2000 以上的打点，留着下回合就打我脸；
        //   ④ 对方前场只剩这一只表侧怪 —— 打掉等于清场。
        private bool IsWorthKilling(ClientCard c)
        {
            if (c == null || !c.IsFaceup() || !c.HasType(CardType.Monster)) return false;
            if (IsResourceMonster(c)) return true;
            if (c.IsExtraCard()) return true;
            if (c.Attack >= 2000) return true;
            return Enemy.GetMonsters().Count(x => x != null && x.IsFaceup() && x.HasType(CardType.Monster)) <= 1;
        }

        private static int TradeValue(ClientCard c)
        {
            int v = c.Attack / 100;
            if (IsResourceMonster(c)) v += 30;
            if (c.IsExtraCard()) v += 25;
            return v;
        }

        // 「卡组引擎」：付自身离场代价（解放 / 送墓 / 除外 / 回手回卡组）才能发动，且效果带「从卡组」。
        //   ★ 救援猫 = 标准形态：「把场上的这张卡送去墓地才能发动。从卡组把 2 只 3 星以下的兽族怪兽特殊召唤」。
        //   ★ 不能简单判「含『从卡组』+『加入手牌/特殊召唤』」——菈碧 ② 的自肃
        //     「自己不能用抽卡以外的方法从卡组把卡加入手牌」、灰流丽「对方把卡从卡组加入手牌」
        //     都会命中，全库 9973 只怪里 1854 只（18.6%）被误判成引擎，判据等于失效。
        //   复用 v5.1 的 CostLeavePattern（已剔掉手卡/墓地/卡组段落），全库命中约 2.7%。
        private static bool IsResourceMonster(ClientCard c)
        {
            if (c == null || !c.HasType(CardType.Monster)) return false;
            string desc = c.Data?.Description;
            if (string.IsNullOrEmpty(desc)) return false;
            if (!desc.Contains("从卡组")) return false;
            return HasCostLeaveFieldEffect(c);
        }

        // ★ v5.31 用户口径（战斗顺序）：
        //   「如果对方场上有连接栗子球之类的怪兽，先用大姐（或者低攻怪兽）攻击，最后再到高攻怪兽攻击。
        //     也可以干脆如果对方场上怪兽攻击力很低，则让攻击力低到高的怪兽顺序攻击
        //     （注意不要让自己的怪兽自杀或者打到防御力比自己怪攻击力高的怪了）。」
        //   为什么要低攻先上：连接栗子球 ①（41999284）「对方怪兽的攻击宣言时，把这张卡解放才能发动。
        //   那只对方怪兽的攻击力直到战斗阶段结束时变成 0」—— 它是**消耗自身**换掉一次攻击。
        //   先用不值钱的攻击者撞上去把它骗掉，后面 4500 的光子咆哮 / 3000 的天霆号就能安全打脸；
        //   反过来先派大哥，等于白送一个战斗阶段。
        //   引擎默认把可攻击的怪按攻击力**从高到低**排（GameAI.OnSelectBattleCmd），
        //   OnSelectAttacker 是唯一能把某只怪提前到 list[0] 的口子，所以顺序只在这里改。
        //   目标选择仍交给 OnSelectAttackTarget：ATK 必须严格大于目标守备力（攻表则比攻击力），
        //   打不穿的目标一律不会被选 —— 这条保证「不自杀、不撞高守备」，这里不重复实现。
        public override ClientCard OnSelectAttacker(IList<ClientCard> attackers, IList<ClientCard> defenders)
        {
            if (Duel.Player != 0 || attackers == null || attackers.Count <= 1)
                return null;

            List<ClientCard> mine = attackers.Where(c => c != null)
                .OrderBy(c => c.GetAttackPower())
                .ThenBy(c => c.Attack)
                .ToList();
            if (mine.Count <= 1) return null;

            List<ClientCard> foes = (defenders ?? new List<ClientCard>())
                .Where(c => c != null && c.IsFaceup() && c.HasType(CardType.Monster)).ToList();

            ClientCard bait = foes.FirstOrDefault(IsBattleBait);
            // 「对方场上怪兽攻击力很低」= 我方**最弱的那只**都能打穿对方全场表侧怪，
            // 且没有里侧怪（里侧的守备力看不见，别当它不存在）。
            bool allWeak = foes.Count > 0
                && defenders.All(c => c == null || c.IsFaceup())
                && foes.Max(f => f.GetDefensePower()) < mine[0].GetAttackPower();
            if (bait == null && !allWeak) return null;

            foreach (ClientCard a in mine)
            {
                if (!HasLegalAttack(a, defenders)) continue;
                if (bait != null)
                    Log("战斗顺序 → 先派 " + a.Name + "（" + a.GetAttackPower() + "）去撞「" + bait.Name
                        + "」，把它的战斗诱发效果骗掉，高攻打点留到后面");
                else
                    Log("战斗顺序 → 对方前场最高才 " + foes.Max(f => f.GetDefensePower()) + "，我方最弱 "
                        + mine[0].GetAttackPower() + " 都打得穿 → 低攻先上：" + a.Name);
                return a;
            }
            return null;
        }

        // 这只怪有没有「打了不亏」的目标：打穿某只 / 能直接攻击。
        // 不满足就跳过它，别让 700 的怪去撞 2800 的墙（自我伤害 + 白送一只怪）。
        private static bool HasLegalAttack(ClientCard attacker, IList<ClientCard> defenders)
        {
            if (attacker == null) return false;
            if (defenders == null || defenders.Count == 0) return attacker.CanDirectAttack;
            int atk = attacker.GetAttackPower();
            foreach (ClientCard d in defenders)
            {
                if (d == null || d == attacker) continue;
                if (atk > d.GetDefensePower()) return true;
            }
            return attacker.CanDirectAttack;
        }

        // 战斗阶段的「诱饵怪」：对手会在**我方攻击宣言时**消耗自身来干扰那次攻击
        //   （连接栗子球：解放自身把攻击力变 0；同类的还有把自身送墓 / 丢弃 / 直接无效攻击的）。
        // 判据用卡文而不是卡号白名单：这类卡在 OCG 里一直有新增，白名单维护不过来。
        private static bool IsBattleBait(ClientCard c)
        {
            if (c == null || !c.IsFaceup() || !c.HasType(CardType.Monster)) return false;
            string desc = c.Data != null ? c.Data.Description : null;
            if (string.IsNullOrEmpty(desc)) return false;
            if (!desc.Contains("攻击宣言时")) return false;
            return desc.Contains("解放") || desc.Contains("送去墓地") || desc.Contains("丢弃")
                || desc.Contains("无效");
        }

        // 攻击目标优先打「值得打」的那只：base 的实现按守备力从高往低试，
        // 对方站一堆杂鱼时会先去啃杂鱼，把救援猫这种引擎留在场上。
        public override BattlePhaseAction OnSelectAttackTarget(ClientCard attacker, IList<ClientCard> defenders)
        {
            if (attacker != null && defenders != null && Duel.Player == 0)
            {
                int atk = attacker.GetAttackPower();
                // ★ v5.31：目标里有「战斗诱饵」（连接栗子球这类，攻击宣言时解放自身把攻击力变 0）
                //   且打得穿 → 这一拳就砸它。顺序已经把最不值钱的攻击者排在前面（OnSelectAttacker），
                //   这里再把目标对准诱饵，才是完整的「先用小怪把它骗掉」。
                ClientCard decoy = defenders
                    .Where(d => d != null && d != attacker && CanBeatBy(atk, d) && IsBattleBait(d))
                    .OrderBy(d => d.GetDefensePower())
                    .FirstOrDefault();
                if (decoy != null)
                {
                    attacker.RealPower = attacker.GetAttackPower();
                    decoy.RealPower = decoy.GetDefensePower();
                    if (OnPreBattleBetween(attacker, decoy))
                    {
                        Log(attacker.Name + " 攻击目标 → 「" + decoy.Name
                            + "」（战斗诱饵，先清掉，免得后面的高攻打点被它吃掉）");
                        return AI.Attack(attacker, decoy);
                    }
                }
                ClientCard best = defenders
                    .Where(d => d != null && d != attacker && d.IsFaceup() && d.HasType(CardType.Monster)
                        && CanBeatBy(atk, d) && IsWorthKilling(d))
                    .OrderByDescending(TradeValue)
                    .FirstOrDefault();
                if (best != null)
                {
                    // 和 base 一样先把 RealPower 摆正再问 OnPreBattleBetween —— 它读的是这两个字段，
                    // 不设就会被上一场战斗的残值带偏（对方无敌 / 危险怪那几条判据全靠它）。
                    attacker.RealPower = attacker.GetAttackPower();
                    best.RealPower = best.GetDefensePower();
                    if (OnPreBattleBetween(attacker, best))
                    {
                        Log(attacker.Name + " 攻击目标 → 「" + best.Name + "」（对方场上最值得战破的一只）");
                        return AI.Attack(attacker, best);
                    }
                }
            }
            return base.OnSelectAttackTarget(attacker, defenders);
        }

        // ★ v5.3：天霆号 3000/3000。
        //   OnSelectPosition 让它在主要阶段 2 以守备落地（要活到对方回合拔 2 清场），
        //   但那个守备会一直挂到下个自己回合 —— 3000 的打点横着一整轮，一点伤害都不打。
        //   实证（replay/优化7.yrp）：天霆号守备横过整个自己回合，战阶全靠别的怪（对方空场时也一样）。
        //   口径：自己回合主阶 1，对方前场空 / 全部压得住 3000 → 翻攻击直接打；
        //        对方站 3000 以上的怪（打过去是平等交换）→ 继续守备。
        //   3000/3000 攻守同值，翻攻击不会掉防守强度，只是少一层「守备表示不吃战斗伤害」的保护，
        //   所以只在压得住对方时才翻。
        private bool ReposZeus()
        {
            if (Duel.Player != 0 || Card == null) return false;
            if (Duel.Phase != DuelPhase.Main1) return false;
            if (!Card.IsFaceup() || !Card.IsDefense()) return false;
            if (!SafeToAttack(Card.Attack))
            {
                Log("天霆号 保持守备（对方前场压不住 " + Card.Attack + "）");
                return false;
            }
            Log("天霆号 转攻击（自己回合主阶 1，打点 " + Card.Attack + " 压得住对方）");
            return true;
        }

        // 场面够不够打：对方前场空，或者我方攻击力压得住对方最高的那只
        private bool SafeToAttack(int myAtk)
        {
            List<ClientCard> em = Enemy.GetMonsters();
            if (em.Count == 0) return true;
            int best = 0;
            foreach (ClientCard c in em)
                best = Math.Max(best, c.IsFaceup() ? c.Attack : Math.Max(c.Attack, 2000));
            return myAtk > best;
        }

        // 能进战阶就进：先攻首回合不能打；对方空场有怪可打、或者这一波够收人头
        private bool GoToBattle()
        {
            if (Duel.Player != 0) return false;
            if (Duel.Turn == 1) return false;
            List<ClientCard> mine = Bot.GetMonsters().Where(c => c.IsFaceup() && c.IsAttack()).ToList();
            if (mine.Count == 0) return false;

            int dmg = mine.Where(c => c.Attack > 0).Sum(c => c.Attack);
            if (dmg >= Enemy.LifePoints) { Log("进战阶（打点 " + dmg + " ≥ 对方 LP）"); return true; }
            if (Enemy.GetMonsters().Count == 0) { Log("进战阶（对方空场）"); return true; }
            // ★ v5.4：打点不够收人头、对方场上也有怪，但只要我方有一只怪能战破对方**高价值**怪兽
            //   （救援猫这种卡组引擎 / 额外怪 / 2000 以上打点），就该进战阶把它换掉。
            //   之前只看「够不够杀」和「对方空不空场」，于是 700 的翼·菈碧对着 300 的救援猫整整一回合没动手。
            foreach (ClientCard c in mine)
            {
                ClientCard trade = FindTradeTarget(c.Attack);
                if (trade != null)
                {
                    Log("进战阶（" + c.Name + " " + c.Attack + " 能战破对方高价值「" + trade.Name + "」）");
                    return true;
                }
            }
            return false;
        }

        // ===== 放置位置 =====
        // ① 大姐必须进 EX 区，否则只是一只 2100 白板；
        // ② 避开无限泡影锁死的纵列（见下方注释）。
        //
        // 无限泡影（10045474）**从场上（盖放后）发动**时，除了无效目标怪兽，还会把它自己那一纵列的
        // 魔法·陷阱效果整列锁死到回合结束：
        //   c10045474.lua → e4:SetTargetRange(LOCATION_SZONE, LOCATION_SZONE) + SetLabel(c:GetSequence())
        //   并且 `Duel.Hint(HINT_ZONE, tp, 0x1 << (c:GetSequence()+8))` 把这个列告诉客户端。
        //   ★ 从手卡直接发动的泡影没有这一段（`if not c:IsStatus(STATUS_ACT_FROM_HAND)`），不锁列。
        // 基类 DefaultExecutor 已经在 OnHintZone 里读到那条 HINT_ZONE，把列号存进
        // infiniteImpermanenceNegatedColumns，并把对方那一侧镜像成我方（`4 - i`）。
        // 这里直接拿来用：魔陷发动 / 盖放时避开这一列，别把卡摆到必被无效的格子上。
        // ★ 整列 5 格全被锁时这里无从避开 → 返回 base 按默认顺序放；那种局面本身发不出来，
        //   由基类 DefaultCheckWhetherSpellActivateWillBeNegated 在各处拦住。
        public override int OnSelectPlace(int cardId, int player, CardLocation location, int available)
        {
            // 大姐必须进 EX 区
            if (location == CardLocation.MonsterZone && cardId == CardId.Jackpot)
            {
                int emz = available & Zones.ExtraMonsterZones;
                if (emz != 0) return emz;
            }

            // 魔陷：避开被无限泡影锁死的列
            if (player == 0 && location == CardLocation.SpellZone
                && infiniteImpermanenceNegatedColumns.Count > 0)
            {
                int allowed = available;
                foreach (int col in infiniteImpermanenceNegatedColumns)
                {
                    if (col >= 0 && col <= 4) allowed &= ~(1 << col);
                }
                if (allowed != 0 && allowed != available)
                {
                    Log("无限泡影锁了第 " + string.Join("、", infiniteImpermanenceNegatedColumns.Select(c => (c + 1).ToString()))
                        + " 列 → 魔陷换一列发动");
                    return allowed;
                }
            }
            return base.OnSelectPlace(cardId, player, location, available);
        }

        public override IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, int hint, bool cancelable)
        {
            // A-1. 针对性终端在「是否发动」时算好的目标：
            //      巨眼① 抢怪 / 混沌大西洲① 装备 / 托勒密② 弹回卡组 / No.76③ 吸墓。
            //      这些询问的候选全是对方场上或墓地的卡，hint 分不出是哪一件事，
            //      交给默认逻辑只会瞎挑（很可能把素材/目标浪费在杂鱼身上）。
            if (_stealTarget != null)
            {
                ClientCard pick = MatchTarget(cards, _stealTarget);
                _stealTarget = null;
                if (pick != null) { Log("巨眼 ① -> " + pick.Name); return new List<ClientCard> { pick }; }
            }
            // 刃/灯 ②「弹回卡组最下面」：决策时就挑好了（希耶娜 → 对方最高攻怪兽 / 库露雪 → 最值钱魔陷）
            //   候选必须是「全在对方场上、只挑 1 张」才算这次询问 —— 万一 ② 被无效、目标没被消费掉，
            //   不能让这个残留标记去污染后面别的选卡。
            if (_bounceTarget != null)
            {
                bool looksLikeBounce = min <= 1 && max == 1 && cards.Count > 0
                    && cards.All(c => c != null && c.Controller == 1
                        && (c.Location == CardLocation.MonsterZone || c.Location == CardLocation.SpellZone
                            || c.Location == CardLocation.FieldZone));
                ClientCard pick = looksLikeBounce ? MatchTarget(cards, _bounceTarget) : null;
                _bounceTarget = null;
                if (pick != null)
                {
                    Log("刃/灯 ② 弹卡选 -> " + pick.Name);
                    return new List<ClientCard> { pick };
                }
            }
            // ★ v5.11 重铠光子龙 ②：候选必然是「对方场上、表侧表示」的卡（脚本 desfilter = IsFaceup），
            //   取发动时点名的那张；对不上就退到在场值最高的那张。
            if (_armorTarget != null)
            {
                bool looksLikeArmor = min <= 1 && max == 1 && cards.Count > 0
                    && cards.All(c => c != null && c.Controller == 1 && c.IsFaceup());
                ClientCard pick = looksLikeArmor
                    ? (MatchTarget(cards, _armorTarget) ?? cards.OrderByDescending(ArmorPreyValue).FirstOrDefault())
                    : null;
                _armorTarget = null;
                if (pick != null)
                {
                    Log("重铠 ② 破坏选 -> " + pick.Name);
                    return new List<ClientCard> { pick };
                }
            }
            // ★ v5.10 征霸龙 ①：引擎分两次问 —— 第 1 次候选是「自己的手卡+场上」（控制器 0），
            //   第 2 次是「对方场上」（控制器 1）。用控制器 + 计数认这两次，挑决策时点名的那张。
            //   形态不符（① 被无效 / 目标没被消费）→ 清标记，别污染后面别的选卡。
            if (_blazePending > 0)
            {
                bool allMine = cards.Count > 0 && cards.All(c => c != null && c.Controller == 0);
                bool allTheirs = cards.Count > 0 && cards.All(c => c != null && c.Controller == 1);
                if (_blazePending == 2 && allMine)
                {
                    ClientCard pick = MatchTarget(cards, _blazeSacrifice)
                        ?? cards.Where(c => c != null && !c.IsCode(CardId.BlazeDragon))
                            .OrderBy(BlazeSacrificeCost).FirstOrDefault();
                    _blazePending = pick != null ? 1 : 0;
                    if (pick != null)
                    {
                        Log("征霸龙 ① 破坏自己 -> " + pick.Name);
                        return new List<ClientCard> { pick };
                    }
                }
                else if (_blazePending == 1 && allTheirs)
                {
                    ClientCard pick = MatchTarget(cards, _blazePrey) ?? BlazePreyIn(cards);
                    _blazePending = 0;
                    _blazeSacrifice = null;
                    _blazePrey = null;
                    if (pick != null)
                    {
                        Log("征霸龙 ① 破坏对方 -> " + pick.Name);
                        return new List<ClientCard> { pick };
                    }
                }
                else
                {
                    _blazePending = 0;
                    _blazeSacrifice = null;
                    _blazePrey = null;
                }
            }
            if (_chaosTarget != null)
            {
                ClientCard pick = MatchTarget(cards, _chaosTarget);
                _chaosTarget = null;
                if (pick != null) { Log("混沌大西洲 ① -> " + pick.Name); return new List<ClientCard> { pick }; }
            }
            if (_omegaTargets != null)
            {
                List<ClientCard> hit = _omegaTargets
                    .Select(t => MatchTarget(cards, t))
                    .Where(c => c != null).Distinct().Take(max).ToList();
                _omegaTargets = null;
                if (hit.Count >= min)
                {
                    Log("托勒密 ② 弹回卡组 " + hit.Count + " 只");
                    return hit;
                }
            }
            // 效果遮蒙者：紧接着的询问必然是「要无效对方哪只怪」→ 直接指刚发动效果的那只
            if (_veilerTarget != null)
            {
                ClientCard want = MatchTarget(cards, _veilerTarget);
                _veilerTarget = null;
                if (want != null)
                {
                    Log("效果遮蒙者 → " + want.Name);
                    return new List<ClientCard> { want };
                }
                // 原目标不在候选里（被换掉 / 已经离场）→ 退到对方表侧怪里攻击力最高的那只
                ClientCard alt = cards.Where(c => c != null && c.Controller == 1
                        && c.Location == CardLocation.MonsterZone && c.IsFaceup() && c.HasType(CardType.Monster))
                    .OrderByDescending(c => c.Attack).FirstOrDefault();
                if (alt != null)
                {
                    Log("效果遮蒙者 → " + alt.Name + "（原目标不在候选里，退到最高的那只）");
                    return new List<ClientCard> { alt };
                }
            }
            // No.76③ 的候选全在对方墓地 → 优先吸「决策时点名的那只」，否则退到攻击力最高的
            if (_utopiaPending)
            {
                _utopiaPending = false;
                ClientCard wanted = MatchTarget(cards, _utopiaTarget);
                _utopiaTarget = null;
                ClientCard best = wanted
                    ?? cards.Where(c => c.Location == CardLocation.Grave && c.Controller == 1)
                        .OrderByDescending(c => c.Attack).FirstOrDefault()
                    ?? cards.FirstOrDefault();
                if (best != null)
                {
                    Log("No.76 ③ 吸 -> " + best.Name);
                    return new List<ClientCard> { best };
                }
            }

            // A0. 愚蠢的副葬：从卡组挑 1 张魔法·陷阱送墓 → 只送辉煌（理由见 BurialActivate）
            if (_burialPending)
            {
                _burialPending = false;
                List<ClientCard> deckCards = cards.Where(c => c.Location == CardLocation.Deck).ToList();
                ClientCard burialPick = deckCards.FirstOrDefault(c => c.IsCode(CardId.Splendor))
                    ?? deckCards.FirstOrDefault(c => c.IsCode(CardId.Stars));   // 辉煌用尽才退而求其次
                if (burialPick != null)
                {
                    Log("副葬 送墓 -> " + burialPick.Name);
                    return new List<ClientCard> { burialPick };
                }
            }

            // A. 从额外卡组挑超量怪（翼③ 加速 / 辉煌① 拉怪）
            //    ★ v5.9：对方回合的加速不再一律千金大奖 —— 交给 PreferredAccelXyz 按「这一手能换到什么」分档：
            //       我方没大姐 + 对方在检索（且自己 EX 区空）→ 大姐（把这次检索换成对方随机抽 1）；
            //       其余（已有大姐 / 对方不是检索 / 对方场上已摆着可弹的卡）→ 千金大奖（拔 2 弹 2）。
            //    自己回合 / 辉煌① 是特招（不触发千金大奖的 ②），一律大姐。
            // ★ 候选里没有本家超量（大姐 / 千金大奖）就不是这两件事 —— 遇到「从额外挑 1 张」的
            //   其它询问（送墓 / 展示 / 选素材）时不要乱插嘴，交给默认逻辑。
            List<ClientCard> extra = cards.Where(c => c.Location == CardLocation.Extra).ToList();
            if (extra.Count > 0 && max >= 1 && min <= 1
                && extra.Any(c => c.IsCode(CardId.Jackpot) || c.IsCode(CardId.Bounty)
                    || c.IsCode(CardId.Utopia76) || c.IsCode(CardId.Omega7)))
            {
                // 辉煌① 已经指定了要拉谁；没指定就是翼③ 加速
                int prefer = _splendorTarget != 0 ? _splendorTarget : PreferredAccelXyz();
                _splendorTarget = 0;
                // v5.15：兜底只用本家两只能量产的终端，别顺手把别的额外怪当成答案
                //（这段也可能被「从额外卡组选 1 张送墓 / 展示」之类的询问撞上）。
                ClientCard pick = extra.FirstOrDefault(c => c.IsCode(prefer))
                    ?? extra.FirstOrDefault(c => c.IsCode(CardId.Jackpot))
                    ?? extra.FirstOrDefault(c => c.IsCode(CardId.Bounty));
                if (pick != null)
                {
                    Log("额外挑超量 -> " + pick.Name);
                    return new List<ClientCard> { pick };
                }
            }

            // B0. 千金大奖 ② 的弹卡目标（v5.15）：候选全在对方场上 → 按 BountyPreyValue 从高到低挑，
            //     不再让引擎顺序决定（旧行为是 `cards.Take(n)`，等于瞎弹）。
            if (_bountyBouncePending > 0 && cards.Count > 0
                && cards.All(c => c != null && c.Controller == 1
                    && (c.Location == CardLocation.MonsterZone || c.Location == CardLocation.SpellZone
                        || c.Location == CardLocation.FieldZone)))
            {
                int take = Math.Min(max, _bountyBouncePending);
                if (take < min) take = min;
                List<ClientCard> picks = cards.OrderByDescending(BountyPreyValue).Take(take).ToList();
                _bountyBouncePending = 0;
                if (picks.Count > 0)
                {
                    Log("千金大奖 ② 弹卡选 -> " + string.Join(" / ", picks.Select(c => c.Name)));
                    return picks;
                }
            }

            // B. 取除超量素材（大姐拔 1 抽 1 / 千金大奖拔 N 弹 N）：数量 = 对手场上卡数，上限为可用素材
            if (cards.Count > 0 && cards.All(c => c.Location == CardLocation.Overlay))
            {
                int threat = Enemy.GetMonsters().Count + Enemy.GetSpells().Count;
                int want = max < threat ? max : threat;
                if (want < min) want = min;
                if (want > 0) return cards.Take(want).ToList();
            }

            // C. 群星① 置顶：从引擎送来的真实卡组列表里挑（AI 自己看不到卡组）
            if (_stackTarget != 0)
            {
                int forced = _stackTarget;
                _stackTarget = 0;
                List<ClientCard> deck = cards.Where(c => c.Location == CardLocation.Deck).ToList();
                if (deck.Count > 0)
                {
                    ClientCard pick = forced > 0
                        ? deck.FirstOrDefault(c => c.IsCode(forced)) ?? PickStackFrom(deck, _starsTopUsed)
                        : PickStackFrom(deck, _starsTopUsed);
                    if (pick != null)
                    {
                        _starsTopUsed.Add(pick.Id);   // ① 同回合不能两次选同名卡
                        Log("群星 置顶 -> " + pick.Name);
                        return new List<ClientCard> { pick };
                    }
                }
            }

            // D. 返 1 张手卡回卡组（帐② / 未眠②）—— ★ v5.22 两个来源分开判：
            //    · 未眠②（`_returnHandIsCity`）：那张卡马上被抽回来，按 `PickCityReturnCard` 挑
            //      「抽回来最赚」的（菈碧 → 西艾萝 → 希耶娜/库露雪 → 手坑沉底），位置也由它决定；
            //    · 帐②（卡文写死「最下面」）：仍按 `HandCardValue` 挑最没用的那张，不设顶/底选项。
            if (_returnHandPending)
            {
                _returnHandPending = false;
                bool fromCity = _returnHandIsCity;
                _returnHandIsCity = false;
                _returnHandToTop = false;

                List<ClientCard> hand = cards.Where(c => c.Location == CardLocation.Hand).ToList();
                ClientCard pick = fromCity ? PickCityReturnCard(hand) : null;
                bool toTop = pick != null && _returnHandToTop;
                if (pick == null) pick = hand.OrderBy(HandCardValue).FirstOrDefault();
                if (pick != null)
                {
                    // lua 里 `Duel.SelectOption(最上面, 最下面)`：0 = 卡组最上面，1 = 最下面。
                    // ★ 只在未眠② 这一支设置 —— 帐② 没有这个选项询问，设了会残留给下一次无关的 SelectOption。
                    if (fromCity) AI.SelectOption(toTop ? 0 : 1);
                    Log("返手卡 -> " + pick.Name
                        + (fromCity ? (toTop ? "（放到卡组最上面，抽回来触发自跳）" : "（放到卡组最下面）") : ""));
                    return new List<ClientCard> { pick };
                }
            }

            return base.OnSelectCard(cards, min, max, hint, cancelable);
        }

        // 反物质龙 ② 的可选追加处理（Duel.SelectYesNo → MSG_SELECT_YESNO）：
        //   「取除的超量素材是怪兽的场合，可以再把和那只怪兽相同种族的 1 只怪兽从卡组送去墓地」。
        // ★ 一律不送。本卡组没有任何墓地利用，堆下去只是白少一张未来的抽卡，
        //   还会把卡组构成暴露给对手。用户口径也是「此时可以不从卡组堆墓」。
        //   desc 精确匹配 id<<4|2，不会误伤其它「是/否」询问。
        public override bool OnSelectYesNo(int desc)
        {
            if (desc == DescAntimatterDump)
            {
                Log("反物质龙 ② → 不从卡组堆墓");
                return false;
            }
            return true;
        }

        public override void OnNewTurn()
        {
            _lightLock = false;
            _stackTarget = 0;
            _starsTopUsed.Clear();
            _returnHandPending = false;
            _returnHandIsCity = false;    // ★ v5.22
            _returnHandToTop = false;     // ★ v5.22
            _cityWaitJackpotLogged = false;   // ★ v5.23：等大姐的日志按回合刷一次
            _splendorTarget = 0;
            _splendorOwnUsed = false;
            _splendorOppUsed = false;
            _splendorGraveUsed = false;
            _burialPending = false;
            _bountyActivated = false;
            _bountyBouncePending = 0;   // v5.15：千金大奖弹卡目标，跨回合不留
            // ★ v5.32：通召雅的解放保护 / 辉煌拉千金的来源标记，跨回合不留
            _carolSummoning = false;
            _carolNoTribute = false;   // ★ v5.35：不解放召唤的 700 标记不跨回合
            _bountyFromSplendor = false;
            // v5.2：陨石计数按回合重置（陨石的条件是「对方这个回合累计召唤/特招 ≥5」）
            _enemySummonCount = 0;
            _nibiruUsed = false;
            _nibiruSeenCount = 0;        // ★ v5.38：陨石的「上次问话时的累计数」也按回合重置
            _nibiruWaitLogged = false;
            // v5.2：副葬 / 群星的顺序状态按回合重置
            _burialUsedThisTurn = false;
            _starsToppedThisTurn = false;
            _orderHoldLogged = false;
            _stealTarget = null;
            _veilerTarget = null;
            _chaosTarget = null;
            _omegaTargets = null;
            _utopiaPending = false;
            _bounceTarget = null;   // v5.5：刃/灯 ② 的弹卡目标，跨回合不留
            _blazePending = 0;      // v5.10：征霸龙 ① 的两段选卡，跨回合不留
            _blazeSacrifice = null;
            _blazePrey = null;
            _armorTarget = null;    // v5.11：重铠 ② 的破坏目标
            // v4.4：水母是手坑，效果只持续「发动的那个回合」，跨回合即失效（RESET_PHASE+PHASE_END）。
            //   每个回合开始清零即可；真正的置位在 OnChaining（对方发动的那一刻）。
            _jellyfish = false;
            _comboTopHeld = false;   // ★ v5.40：水母 combo 的「牌堆顶留着」不跨回合
            // ★ v5.41：「盖辉煌就过」的回合锁定与日志也按回合清（hold 只在本回合内生效）
            _setPassTurn = -1;
            _holdPassLogged = false;

            // ★ v5.29：锁鸟的两道闸门状态，跨回合不留 ——
            //   `_lastChainController` 若不清，上一回合对方最后发动过效果会让本回合的锁鸟
            //   误判成「对面刚动过」；`_jackpotReplacedSearch` 同理，只服务它自己那一瞬间。
            _lastChainController = -1;
            _jackpotReplacedSearch = false;
            _squizContinuous = false;   // ★ v5.36：朔的持续抽卡只持续「发动的那个回合」，跨回合清
            // ★ v5.37：小丑与锁鸟的封锁（不能从卡组加手卡 + 不能抽卡）到回合结束复位 → 回合号兜底清零
            _drollTurn = -1;
            _cityDrollLogged = false;
            _cityBurialLogged = false;   // ★ v5.43：未眠给副葬让路的日志也按回合重置

            base.OnNewTurn();
        }

        // ★ v4.4：水母（マルチャミー・プルリア 84192580）是从手牌丢弃发动的**手坑**，
        //   本身不会作为怪兽留在场上 —— 旧版用 Enemy.GetMonsters() 检测永远命中不了，
        //   导致 hold / combo 从不触发。这里改成在连锁建立时直接认牌子：
        //   对方（player == 1）发动 84192580 → 本回合剩余时间我方从手牌召唤 / 特招都会让对方抽 1。
        //   效果到本回合结束就没了，下个回合 OnNewTurn 自动清零。
        public override void OnChaining(int player, ClientCard card)
        {
            base.OnChaining(player, card);

            // ★ v5.29：记下「最后一手是谁发的」。锁鸟是延迟诱发，问我们的时候连锁已经结算完、
            //   `Util.GetLastChainCard()` 已经空了，只能靠这个字段判断「刚才是不是我方自己的动过」。
            _lastChainController = player;

            // ★ v5.37：小丑与锁鸟一发动（无论谁丢的 —— 效果对**双方**生效，见 c94145021.lua 的 operation），
            //   本回合就直接进「不能从卡组把卡加入手卡 + 不能抽卡」状态。
            //   记下回合号，未眠之城据此整回合不动（见 `CityActivate`）。两个号都认，见 `LockBirdAlt`。
            if (card != null && (card.IsCode(CardId.LockBird) || card.IsCode(CardId.LockBirdAlt)))
            {
                _drollTurn = Duel.Turn;
                Log("小丑与锁鸟 → " + (player == 0 ? "我方" : "对方")
                    + "发动，本回合双方都不能从卡组把卡加入手卡 / 不能抽卡 → 未眠之城整回合不动");
            }

            // 对方重新起一条链 = 一个独立的新动作；大姐那次替换的「抽卡归属」到此作废。
            if (player == 1) _jackpotReplacedSearch = false;

            if (player == 1 && card != null && card.IsCode(CardId.Jellyfish))
            {
                _jellyfish = true;
                // ★ v5.41：口径（用户原话）——「中了水母后如果你手牌有辉煌，那么可以直接盖放陷阱
                //   （有群星的话置顶照旧），其他动作一律不做，然后回合结束等对方动作」。
                //   所以这里只看**手里有没有辉煌**：有就走「盖辉煌 + 群星①置顶 → 直接过」，
                //   没有再退回 v5.40 的「铺到够用就停手 / 直接叠大姐」。
                //   注意此刻往往还是**对方回合**（水母是手坑），`_holdMode` 恒 false，
                //   所以日志不能拿 `_holdMode` 当判据，要看手牌。
                bool hasSplendorInHand = Bot.Hand.Any(c => c != null && c.IsCode(CardId.Splendor));
                Log("水母 → 对方发动（本回合我方从手牌召唤/特招 → 对方抽 1） hold=" + _holdMode
                    + " combo=" + _jellyfishCombo
                    + (hasSplendorInHand
                        ? "（手里有陷阱辉煌 → 我方回合只盖它 + 群星①置顶，其余动作一律不做，等对方回合用辉煌拉大姐）"
                        : "（无陷阱辉煌 → 铺到「大姐在场」或「本家 2 只」就停手，直接叠大姐；有群星则置顶留到对方回合）"));
            }

            // ★ v4.8：对方从**墓地**发动效果 → 认定它在吃墓地，No.76 谐调光师才有意义。
            //   （墓地里的卡发效果时 Location 仍是 Grave，能可靠区分「手牌/场上发动」）
            if (player == 1 && card != null && !_enemyGravePlay && card.Location == CardLocation.Grave)
            {
                _enemyGravePlay = true;
                Log("对方墓地利用暴露（" + card.Name + " 从墓地发动）→ 之后才考虑 No.76");
            }
        }

        // ★ v4.8：对方从墓地特招 / 把墓地的卡拿回场上，同样算「吃墓地」的硬证据。
        public override void OnMove(ClientCard card, int previousControler, int previousLocation, int currentControler, int currentLocation)
        {
            base.OnMove(card, previousControler, previousLocation, currentControler, currentLocation);

            // ★ v5.2：陨石的计数 —— 对方「召唤·特殊召唤成功」的累计次数。
            //   OnMove 里 怪兽区以外 → 怪兽区 = 一次召唤/特招成功（含衍生物、额外卡组召唤、墓地复活）。
            //   怪兽区内部的位置移动 / 控制权转移（previousLocation 也是怪兽区）不算，别重复计。
            if (card != null && currentControler == 1
                && currentLocation == (int)CardLocation.MonsterZone
                && previousLocation != (int)CardLocation.MonsterZone)
            {
                _enemySummonCount++;
            }

            // ★ v5.1：记下「对方本回合最后摆上场的那只怪」。
            //   提前交遮蒙者时优先挑它 —— 典型场景是对方通召救援猫/救援兔这种
            //   「cost 离场才能发动」的怪，刚摆上来就说明这回合要动它。
            if (card != null && card.Id != 0 && currentControler == 1
                && currentLocation == (int)CardLocation.MonsterZone
                && previousLocation != (int)CardLocation.MonsterZone)
            {
                _enemyLastSummonedId = card.Id;
                _enemyLastSummonedTurn = Duel.Turn;
            }

            if (card == null || _enemyGravePlay) return;
            // v5.9：这里原来记 `_rabiArrivedTurn`（菈碧落场回合），用来在对方回合认 ② 抽 1；
            //   现在 ②/③ 靠 SelectingEffectYn 分流，这段可以去掉（见 RabiActivate）。
            if (previousControler != 1) return;
            if (previousLocation == (int)CardLocation.Grave
                && (currentLocation == (int)CardLocation.MonsterZone || currentLocation == (int)CardLocation.SpellZone))
            {
                _enemyGravePlay = true;
                Log("对方墓地利用暴露（" + card.Name + " 从墓地回场）→ 之后才考虑 No.76");
            }
        }

        // ================= 工具 =================

        private bool HasFreeMonsterZone()
        {
            return Bot.GetMonstersInMainZone().Count < 5;
        }

        // ★ v5.37：本回合小丑与锁鸟的效果在不在适用 —— 双方都不能从卡组把卡加入手卡，**而且不能抽卡**
        //   （c94145021.lua 的 operation 挂 `EFFECT_CANNOT_TO_HAND` + `EFFECT_CANNOT_DRAW`，都是双方、
        //   RESET_PHASE+PHASE_END）。置位只在 `OnChaining` 认到 94145021 / 94145022 那一刻。
        //   只按回合号比，跨回合自动失效；`OnNewTurn` 另有兜底清零。
        private bool DrollAppliedThisTurn()
        {
            return _drollTurn == Duel.Turn;
        }

        // 对方场上还有几张卡（怪 + 魔陷）
        private int EnemyFieldCount()
        {
            return Enemy.GetMonsterCount() + Enemy.GetSpellCount();
        }

        // ★ v5.3：这只怪的效果**已经被无效**（Disabled 标记已挂上）→ 发动也只是白付 cost。
        //   实证（replay/优化7.yrp）：被提前无效的千金大奖照样拔 2 素材想弹卡，结果整条效果被无效，
        //   素材白扔、对方场面一点没动。效果被无效时，cost 照付、效果不处理 —— 纯亏。
        //   手上/墓地的卡 Disabled 恒为 0，所以这个判断加在通用入口也不会误伤。
        private bool EffectNegated()
        {
            if (Card == null || !Card.IsDisabled()) return false;
            Log(CardName() + " 效果已被无效 → 不发动（省下 cost）");
            return true;
        }

        // 当前是否处于连锁中（C2 及以后）。
        // 服务端先发 MSG_CHAINING 把卡加进 CurrentChain，**再**发 MSG_SELECT_CHAIN 问我们要不要连锁，
        // 所以被问「要不要连锁」时 Count 一定 ≥ 1；而自由时点（我们可以自己开 C1）时一定是 0。
        private bool InChain()
        {
            return Duel.CurrentChain != null && Duel.CurrentChain.Count > 0;
        }

        // 当前连锁上有没有「不取对象的群体清场」，而且它会拆到 want 这个区域。
        //   want = RaiseMoonScriptWipe.Monster  → 拆怪兽区（黑洞 / 雷击 / 激流葬 / 圣防 / 天霆号）
        //   want = RaiseMoonScriptWipe.SpellTrap → 拆魔陷区（大岚 / 羽毛扫）
        // ★ 取对象的效果不用在这里管 —— 它们会进 Duel.ChainTargets，由 Util.IsChainTarget 判定。
        //   只有「破坏场上所有卡」这类不发 MSG_BECOME_TARGET 的清场，才必须靠这个提前识别。
        private bool ChainHasZoneWipe(int want)
        {
            if (Duel.CurrentChain == null || Duel.CurrentChain.Count == 0) return false;
            foreach (ClientCard c in Duel.CurrentChain)
            {
                if (c == null) continue;
                // 只统计「对方发动的、会拆到我们这一侧」的群体清场
                bool enemyCard = c.Controller != 0;
                if (!RaiseMoonScriptWipe.Wipes(c.Id, want, enemyCard)) continue;
                Log("检测到群体清场：" + c.Name);
                return true;
            }
            return false;
        }

        // 自己在场上的这张卡，会不会在本次连锁结束时消失（被取对象 or 被清场）
        private bool SelfDoomedOnField(int zone)
        {
            return Util.IsChainTarget(Card) || ChainHasZoneWipe(zone);
        }

        // ★ v5.9：抽卡类效果**唯一**的时机判据（帐③ / 大姐拔1抽1 / 墓地辉煌② 全用它）。
        //   「对方真的把东西摆到场上了」：
        //     · 怪兽区有怪（含里侧盖怪 —— 那是已经落地的布置，希耶娜 ② 弹得到）；
        //     · 魔陷区有**表侧**的卡：永续魔法 / 永续陷阱 / 装备 / 场地魔法 / 灵摆刻度（库露雪 ② 弹得到）。
        //   不算：魔陷区的里侧盖卡；发完就进墓的通常魔法 / 通常陷阱（对方开一张滤抽，场上还是空的）。
        //   用户口径（replay/优化10）：「对方空场你抽到希耶娜和库露雪换不到对方场上的卡 —— 血亏。
        //   应该是对方把怪登场、或者在魔陷区贴了永续魔法陷阱 / 场地 / 灵摆之后再发动抽卡效果。」
        //   ⇒ 原来那个更宽松的 `EnemyHasBoard()`（连里侧盖卡都算）已经合并到这里，不再单独保留。
        private bool EnemyDeployed()
        {
            if (Enemy.GetMonsterCount() > 0) return true;
            return Enemy.GetSpells().Any(c => c != null && c.IsFaceup());
        }

        // 手上有没有「丢弃自身 → 抽 1」的 7 星（朔 / 刃 / 灯）。
        // 有它才能接上「辉煌拉千金大奖 → 丢手抽 1 → 抽到本家自跳 → 千金大奖拔素材弹卡」那条链。
        private bool HandHasDiscardDraw()
        {
            return Bot.Hand.Any(c => c.IsCode(CardId.Squiz) || c.IsCode(CardId.Hiena) || c.IsCode(CardId.Kurusu));
        }

        // 通召雅：只走「不解放直接召唤」这条路，绝不接受解放召唤。
        // 卡萝尔 ② 的脚本是 `EFFECT_SUMMON_PROC` + `s.ntcon` 里的 `minc == 0`（c11333460.lua）：
        //   只有引擎把「最少解放数」算成 0 时「不用解放」这条路才成立；场上有可解放的怪时 minc > 0，
        //   condition 为 false，引擎就只给「解放召唤」这一条。
        // ★ 为什么不能靠「选哪条召唤条目」来判断：
        //   协议里 MSG_SELECT_IDLECMD 前 5 组（含召唤组）只有 code / controller / location / sequence，
        //   **不带 desc**，同一条目的两种召唤方式在协议层完全无法区分；而响应用的是列表下标，
        //   一旦有两条就必然要猜。所以只能反过来保证「引擎只有一条可选」。
        // ★ 祭品只能来自我方场上（主怪区 + EX 区都算），因此：
        //   **我方场上没有任何怪兽** ⇒ minc 必然为 0 ⇒ 引擎给不出解放召唤条目，召唤只可能走不解放。
        //   这一条同时挡住了「大姐在 EX + 主怪区 1 只」那种刚好凑够 2 只祭品的场面
        //   （EX 区的怪同样是合法祭品，不能按主怪区计数）。
        //   ⇒ 现行规则：场上有任何怪兽就不通召卡萝尔，宁可留在手里，绝不再拿场面换 1400。
        private bool SummonCarol()
        {
            // ★ v4.4 / v5.40：水母停铺（hold 或 场面已够用）→ 连通召雅都不做（通召也是从手牌登场，喂水母）
            if (_jellyfishFreeze)
            {
                Log("雅 水母停铺 → 不通召（通召也是从手牌上怪）" + JellyfishFreezeWhy());
                return false;
            }
            if (!HasFreeMonsterZone()) return false;

            bool noTribute = CanSummonCarolWithoutTribute();
            if (!noTribute)
            {
                Log("雅 我方场上有怪兽（含 EX 区大姐）→ 这次只能是解放召唤，任何情况都不做，不召");
                return false;
            }
            _carolSummoning = true;
            _carolNoTribute = true;   // ★ v5.35：这次是不解放召唤 → 落地面板 700，不是卡面 1400
            Log("通召 雅·卡萝尔（不解放，攻击力变 700，不花祭品）");
            return true;
        }

        // ★ v5.44：我方场上没有任何怪兽 → 引擎的召唤条目只可能是 ② 的「无解放召唤」。
        //   祭品只能来自我方场上（主怪区 + EX 区都算），没怪就凑不出解放召唤的 cost，minc 必然为 0。
        //   这是协议层唯一能「保证」不献祭的判据：召唤条目不带 desc，没法从条目本身区分两种召唤方式。
        private bool CanSummonCarolWithoutTribute()
        {
            return Bot.GetMonsters().Count == 0;
        }

        // ★ v5.34 用户口径：「如果升月先攻有卡萝尔，尽量第一个动作先直接召唤卡萝尔抽卡。」
        //   先攻 T1 的第一次 idle 询问就通召她：③ 抽 1 是本卡组唯一不花钱的续航，
        //   越早抽越早把本家抽上来接着自跳铺场；自肃（本回合不能再从卡组加入手卡 + 只能光属性超量）
        //   在先攻首回合几乎没有代价（本家额外全是光属性，也没有检索卡）。
        //   后攻 / 其它回合仍走下面那条排在「副葬 / 群星 / 场地」之后的注册，保持 v5.15 的抽卡顺序。
        private bool SummonCarolFirst()
        {
            if (Duel.Turn != 1 || Duel.Player != 0) return false;
            return SummonCarol();
        }

        private int FreeEmzCount()
        {
            return 2 - Bot.GetMonstersInExtraZone().Count;
        }

        // 只有「一掷乾坤」是魔法师族 —— 未眠②/群星② 那条分支只能靠它
        private bool HasSpellcasterXyz()
        {
            return Bot.GetMonstersInMainZone().Any(c => c.IsCode(CardId.Jackpot))
                || Bot.GetMonstersInExtraZone().Any(c => c.IsCode(CardId.Jackpot));
        }

        // 手坑只能对着对方的效果交
        private bool EnemyActivated()
        {
            ClientCard last = Util.GetLastChainCard();
            return last != null && last.Controller == 1;
        }

        // ★ v5.29 用户口径：「手坑不是对应自己效果发动连锁的，是留给干扰对面的。」
        //   这条只管**连锁里**的判断（连锁为空说明是自由时点 / 延迟诱发，另按各自判据处理）。
        //   连锁的最后一环是我方的卡 → 手坑一律不接：那是我方自己的结算，锁下去只会锁到自己。
        private bool ChainingToOwnCard()
        {
            if (!InChain()) return false;
            ClientCard last = Util.GetLastChainCard();
            return last != null && last.Controller == 0;
        }

        // ================= 手坑 =================

        // 增殖的G：唯一的持续型手坑，对面一开始动就丢，越早越赚。
        // 增殖的G（23434538）
        //   手牌 QUICK：给对方看 → 这个回合对方每次特殊召唤成功，自己抽 1。
        //
        // ★ v5.12 用户口径（replay/优化12）：
        //   「增殖的G 不要在回合一开始就发动，最好在对方有要进行特殊召唤的动作时连锁
        //     （比如 C1 对方要特殊召唤、C2 增殖的G）。」
        //   ⇒ 只在**连锁里**发（C2 及以上），而且链上最后一环必须是对方的卡；
        //     不再抢「对方回合开始 / 自由时点」那一下。
        //   为什么：对方一进主阶就丢 G，等于把这张牌提前亮出来换一个可能根本不会发生的特招；
        //   等他把「要特招」的动作摆上桌再跟，才是「你跳怪 → 我这回合开始抽」的交换。
        //   ★ 局限：AI 拿不到对方效果的分类，判不出「这一环到底会不会特招」，
        //     所以判据只能是「对方在发动效果」。宁可晚一手，也不在空时点上先亮牌。
        //   ★ v5.20 用户重申：「G 的逻辑就是正常如果对方要发动进行特殊召唤的效果，那么 G 跟连锁。」
        //     即维持上面这条判据。唯一补的是**注册顺序**：G 现在排在灰流丽 / 幽鬼兔 / 遮蒙者之后 ——
        //     同一张检索既有灰流丽又有 G 时，先交灰流丽（无效掉检索，G 留着应付后面的展开）；
        //     灰流丽不在 / 已用掉，才让 G 跟这一链。
        private bool UseMaxxC()
        {
            if (Duel.Player != 1) return false;
            if (!InChain())
            {
                Log("手坑 增殖的G → 对方只是自由时点，等它发效果要特招再连锁");
                return false;
            }
            ClientCard last = Util.GetLastChainCard();
            if (last == null || last.Controller != 1)
            {
                Log("手坑 增殖的G → 连锁上最后一环不是对方的卡，不跟");
                return false;
            }
            Log("手坑 增殖的G（连锁对方「" + (last.Name ?? "?") + "」）");
            return true;
        }

        private bool UseAshBlossom()
        {
            // ★ v5.21 用户口径（控制对方资源优先）：
            //   「对方手牌很少、我方场上有大姐、手里有灰流丽，对方发动检索加手效果 →
            //    此时应该先灰流丽无效对方检索效果，而不是让大姐改效果。」
            //   大姐 e5 是 `Duel.ChangeChainOperation` + `Duel.Draw(tp,1)`（tp = 原效果发动者 = 对方），
            //   精准检索只是被**换成一次处理**（对方随机抽 1）—— 手牌少的时候那一张就是他的救命资源，
            //   而灰流丽是「彻底无效、连抽都不给」。资源交换的总纲下，这种情况下灰流丽先上。
            //   ⇒ 阈值从「≤2 才交」放宽到「≤3 就交」；对方手牌 >3 时仍让大姐接管（那一张随机
            //     对他不痛不痒，灰流丽留着应对后续展开更值）。
            //
            // ★ v5.21 修掉的另一个漏洞：以前 `UseNegate` 里挂着「只在对方回合」的闸门，
            //   我方回合对方从墓地/场上发动检索时灰流丽一律不响应（日志里只剩一行大姐 desc=…），
            //   等于把这条链路整个让给了大姐的替换。现在灰流丽不受回合限制。
            if (!EnemyActivated()) return false;                 // 我方自己的检索不问灰流丽（也不刷日志）
            if (AshYieldToJackpot())
            {
                Log("灰流丽 → 大姐在场、对方手牌 " + Enemy.Hand.Count + " 张（>3，随机抽1 不痛）→ 留着，让大姐改效果");
                return false;
            }
            // 其余场合：无效「从卡组加入手卡 / 从卡组特招 / 从卡组送墓」。
            // AI 拿不到对方效果的分类，所以只要对面在发动效果就交 —— 这三类占了绝大多数。
            bool ok = UseNegate("灰流丽", true);
            if (ok) _ashUsedTurn = Duel.Turn;
            return ok;
        }

        // 灰流丽此刻「不想交」的唯一一种情况：EX 区大姐在场、对方手卡还 >3。
        //   （大姐 e5 会把对方的检索整个换成「对方随机抽 1 张」，等于已经打掉了精准性，
        //    手牌宽裕的对方不缺这一张，灰流丽留着应付对方后面的展开更值。）
        //   ★ v5.21：阈值 2 → 3。对方手牌 ≤3 时改成「灰流丽先无效」——
        //     这时他每少一张牌都是实质性的资源差，比「随机抽 1」控得死。
        private bool AshYieldToJackpot()
        {
            return Bot.GetMonstersInExtraZone().Any(c => c.IsCode(CardId.Jackpot))
                && Enemy.Hand.Count > 3;
        }

        // ★ v5.20：灰流丽此刻会不会真的交出去 —— 与 `UseAshBlossom` 同一套判据（朔 靠它让位）。
        //   用户口径：对方回合手卡同时有灰流丽和朔时，**第一个检索先给灰流丽无效**；
        //   对方再开第二个检索，灰流丽已经用掉（1 回合 1 次），这时才轮到朔挂持续抽卡。
        //   所以朔不能问「有没有灰流丽」，要问「灰流丽这一环会不会真的交」——
        //   大姐在场那种「主动不交」的情况要让朔照常跟链，免得两头都空着。
        private bool AshWillTakeChain()
        {
            if (!Bot.Hand.Any(c => c != null && c.IsCode(CardId.AshBlossom))) return false;
            if (_ashUsedTurn == Duel.Turn) return false;        // 本回合已经交过
            if (NegateAlreadyInChain()) return false;           // 这条连锁上已经有一张我方的无效系
            if (AshYieldToJackpot()) return false;              // 大姐会把它换成随机抽 1 → 留着
            ClientCard last = Util.GetLastChainCard();
            return last != null && last.Controller == 1;        // 链上最后一环是对方的卡才谈得上「交」
        }

        // 小丑与锁鸟（94145021）
        //   ① 自己·对方回合，对方在抽卡阶段以外「从卡组把卡加入手卡」的场合，把这张卡从手卡送墓 →
        //      这个回合双方都不能从卡组把卡加入手卡。
        //
        // ★ 用法（按用户口径）：**后攻**专用 —— 对面先手 T1 一旦发起检索，就把锁鸟丢下去，
        //   把他后面整条展开线全部掐死（后续所有从卡组加手卡的效果都过不去）。这是它唯一的高价值时点。
        // ★ 先攻局（对面 T2）不丢：那时我方场上往往已经站着西艾罗、墓地里躺着辉煌，
        //   这个封锁会把「西艾罗③ 抽 1」「辉煌② 除外抽 1」这些自家续航一起停掉，
        //   掐对面一个效果却断自己一条链路，不划算 —— 留着手牌，等对面真正收不住的时候再说。
        private bool UseLockBird()
        {
            if (Duel.Player != 1) return false;          // 只在对面的回合

            // ★ v5.29 用户口径：「手坑不是对应自己效果发动连锁的，是留给干扰对面的。」
            //   实证（replay 23:59 / 00:02 两局）：大姐 e5 把对方的检索换成「对方随机抽 1」之后，
            //   锁鸟紧跟着跳了出来（`手坑 小丑与锁鸟（封锁本回合从卡组加手卡）`）——
            //   对方那次「从卡组加手卡」是**我方大姐造成的**，锁鸟接的是我们自己的结算。
            //   而且锁鸟的 operation 会同时挂 `EFFECT_CANNOT_TO_HAND` + `EFFECT_CANNOT_DRAW`（双方），
            //   这一手连锁下去等于把自家「大姐拔1抽1 / 西艾萝③ / 辉煌② / 未眠② / 群星②」全停到回合结束。
            //   两道闸门：
            //   ① 这一手接的必须是对面的卡（连锁里看最后一环；连锁已清空则看最后一次发动是谁的）；
            //   ② 对方这次加手卡不能是大姐替换出来的（`_jackpotReplacedSearch`）。
            if (InChain())
            {
                ClientCard lastChain = Util.GetLastChainCard();
                if (lastChain == null || lastChain.Controller != 1)
                {
                    Log("手坑 小丑与锁鸟 → 连锁上最后一环是我方自己的卡，手坑只留给干扰对面 → 留着");
                    return false;
                }
            }
            else if (_lastChainController != 1)
            {
                Log("手坑 小丑与锁鸟 → 上一次发动是我方自己的效果（不是对面），锁鸟不接自己的抽卡 → 留着");
                return false;
            }

            if (_jackpotReplacedSearch)
            {
                _jackpotReplacedSearch = false;      // 一次性：这次「对方抽卡」确实是大姐造成的
                Log("手坑 小丑与锁鸟 → 对方这次抽卡是大姐把检索换成「随机抽 1」造成的，不接自己的效果 → 留着");
                return false;
            }

            if (Duel.Turn == 1)
            {
                // 对面是先手第一回合 = 我们是后攻。这一段就是锁鸟的黄金时点。
                Log("手坑 小丑与锁鸟（后攻 T1，掐死对面整套展开）");
                return true;
            }
            // 非后攻首回合：我方还有抽卡引擎等着用 → 留着，别把自己也锁了
            //   ★ v5.29：`ownDrawWanted` 原来漏了**站着的大姐**（EX 区还带素材 = 每回合白抽 1），
            //     也漏了未眠之城（② 那两条都是抽卡）。锁鸟连抽卡一起封，漏掉它们等于自断主引擎。
            bool ownDrawWanted = Bot.GetMonstersInExtraZone()
                    .Any(c => c != null && c.IsCode(CardId.Jackpot) && c.HasXyzMaterial(1))
                || Bot.GetMonsters().Any(c => c.IsCode(CardId.Siero))
                || Bot.Graveyard.Any(c => c.IsCode(CardId.Splendor))
                || Bot.GetSpells().Any(c => c != null && c.IsCode(CardId.City));
            if (ownDrawWanted)
            {
                Log("手坑 小丑与锁鸟 → 我方还有抽卡引擎，留着");
                return false;
            }
            Log("手坑 小丑与锁鸟（封锁本回合从卡组加手卡）");
            return true;
        }

        private bool UseGhostRabbit()
        {
            // 幽鬼兔：破坏「发动效果的那张场上卡」。
            // ★ 对方那张卡如果是「不会被效果破坏 / 可以代替破坏」，砸上去纯亏一张手牌：
            //   卡不会走，对方效果照样处理完，我们还少一个干扰。
            ClientCard last = Util.GetLastChainCard();
            if (last != null && last.Controller == 1 && last.IsOnField() && IsDestroyImmune(last))
            {
                Log("手坑 幽鬼兔 → 对方 " + last.Name + " 不会被效果破坏，留着");
                return false;
            }
            return UseNegate("幽鬼兔");
        }

        // 场上这张卡现在是不是「不会被效果破坏」。两个来源都要查：
        //   1. 它**自己**的 lua 脚本里注册了免疫 / 代替破坏；
        //   2. 它**身上装备的卡**把免疫赋予它了 —— 典型：太阴之咒眼（44133040）
        //      `EFFECT_TYPE_EQUIP` + `EFFECT_INDESTRUCTABLE_EFFECT` + `EFFECT_CANNOT_BE_EFFECT_TARGET`，
        //      装备怪兽既不会被效果破坏、我方还取不了它做对象。
        // ★ 只看怪兽自己的脚本会漏掉第 2 层，以前就是在「装备了太阴之咒眼」的场面上白丢过幽鬼兔。
        private bool IsDestroyImmune(ClientCard c)
        {
            if (c == null) return false;
            if (RaiseMoonScriptWipe.CannotBeDestroyedByEffect(c.Id)) return true;
            return c.EquipCards.Any(e => e != null && RaiseMoonScriptWipe.CannotBeDestroyedByEffect(e.Id));
        }

        // ===== 陨石：原始生命态 尼比鲁（27204311）=====
        // 卡文：① 对方把 5 只以上的怪兽召唤·特殊召唤的（自己·对方回合的）主要阶段才能发动。
        //   自己·对方场上的表侧表示怪兽尽可能解放，这张卡从手卡特殊召唤；
        //   那之后在对方场上把 1 只衍生物特殊召唤（攻守 = 被解放怪兽原本攻守的合计）。
        //
        // ★ v5.20 用户口径：数到第 5 只**不无条件发**，先做一次场面资源比较 ——
        //   · 对方场面资源比我方多 → 可以发（解放全场 = 拿更少的场面换掉对方更多的场面）；
        //   · 对方场面很空、我方场上怪兽很多 → 解放自己的全场得不偿失，**留着**。
        //   卡文只解放**表侧表示**怪兽：里侧盖怪不参与；EX 区的大姐不受其他卡效果影响也解放不到，
        //   这两类都不算「我方的损失」。比较对象只用怪兽，魔陷本来就不受影响。
        //
        // ★ v5.38 用户口径（replay _LastReplay 02:36 局）：
        //   「这时最好等对方把怪特招到场上之后再开陨石效果（毕竟陨石是满足条件之后任意主阶时点发动），
        //     你先开是解放不了后续效果特殊召唤的怪的」
        //   ⇒ 在上面的门槛之外再加一道「**不在连锁里开**」：连锁后发先结算，插 C2 陨石等于让陨石先跑，
        //     随后 C1 结算特招出来的怪落在解放之后、全部逃掉。陨石是「满足条件后任意主阶时点」的诱发即时效果，
        //     没有任何理由抢这一下 —— 等连锁清空、对方把这一波的怪都摆到场上，再以 C1 开。
        private bool NibiruActivate()
        {
            if (Card == null || Card.Location != CardLocation.Hand) return false;
            if (_nibiruUsed) return false;                                   // 卡名 1 回合 1 次
            // ★ v5.29：手坑只接对面的卡 —— 这条链是我方自己的结算就不插手。
            if (ChainingToOwnCard())
            {
                Log("陨石 → 这一链接的是我方自己的卡，手坑只用来干扰对面 → 留着");
                return false;
            }
            if (_enemySummonCount < 5) return false;                         // 没到 5 只，条件不成立
            if (Duel.Phase != DuelPhase.Main1 && Duel.Phase != DuelPhase.Main2) return false;  // 只在主要阶段

            // ★ v5.38 用户口径（replay _LastReplay 02:36 局）：
            //   「这时最好等对方把怪特招到场上之后再开陨石效果（毕竟陨石是满足条件之后任意主阶时点发动），
            //     你先开是解放不了后续效果特殊召唤的怪的」
            //   连锁是**后发先结算**：现在插 C2 陨石，陨石先结算解放全场，然后 C1 那条对方效果再特招出来的怪
            //   正好落在解放之后 —— 一张都吃不到（典型：对方 C1 死者苏生 / 各种「从卡组·墓地特招 1 只」，
            //   我方 C2 陨石 = 解放了个空气，白白亮牌）。陨石既然「满足条件后的任意主阶时点」都能发，
            //   就不抢这一下：等这条链结算干净、怪都落到场上，再在自由时点（C1）开，解放数量最多。
            if (InChain())
            {
                ClientCard top = Util.GetLastChainCard();
                if (!_nibiruWaitLogged)
                {
                    _nibiruWaitLogged = true;
                    Log("陨石 → 连锁上还有" + (top != null && top.Controller != 0 ? "对方" : "我方")
                        + "的「" + (top != null ? (top.Name ?? "?") : "?") + "」没结算，现在开是 C"
                        + (Duel.CurrentChain.Count + 1)
                        + "：陨石先结算，这条效果随后特招出来的怪落地后解放不到 → 等连锁清空、怪都摆到场上再开");
                }
                return false;
            }

            // 对方场上没有表侧怪时开它 = 只把自己场上的怪解放掉，纯亏。
            int faceup = Enemy.GetMonsters().Count(c => c != null && c.IsFaceup());
            if (faceup == 0)
            {
                if (!_nibiruWaitLogged)
                {
                    _nibiruWaitLogged = true;
                    Log("陨石 对方场上没有表侧怪 → 留着（开了只会解放自己的怪）");
                }
                return false;
            }

            // ★ v5.38：等对方把这一波铺完 —— 上一次来问之后对方又特招了新怪，说明它还在铺，
            //   这一手先不发；等下一个时点（对方的召唤数不再涨 = 这一波停了）再开，那时解放的怪最多。
            //   保命例外：LP 已被压到 3000 以下就不再等，先把场面清了再说。
            if (_enemySummonCount > _nibiruSeenCount)
            {
                _nibiruSeenCount = _enemySummonCount;
                if (Bot.LifePoints > 3000)
                {
                    if (!_nibiruWaitLogged)
                    {
                        _nibiruWaitLogged = true;
                        Log("陨石 → 对方本回合已累计特招 " + _enemySummonCount
                            + " 只、刚从上次问话到现在又铺了新怪（这一波还没停）→ 等它把怪都摆到场上再开");
                    }
                    return false;
                }
            }

            int ownLoss = NibiruSideValue(true);
            int enemyGain = NibiruSideValue(false);
            // ★ v5.39 用户口径：「陨石不要乱解放自己场上的怪，要比较双方怪兽资源差」——
            //   两层判据，缺一不发：
            //   ① 老门槛（v5.20 原样保留）：对方表侧资源必须**严格大于**我方会被解放的资源；
            //   ② 新账本：把两边「换回来的东西」也算进来，只有净收益为正才动手。
            //      · 我方换回「尼比鲁」3000/600 → 同 `NibiruSideValue` 权重 = 3000 + 800；
            //      · 对方换回 1 只衍生物，卡文：「攻·守变成这个效果解放的怪兽的原本攻击力·守备力各自合计」
            //        → 按 attack 合计 + 800 估（守备力不计，按保守的低估处理）。
            //      也就是说：我方每多留一只怪，不只是「我方损失」那一项，同时还在**替对方做大衍生物**——
            //      双重代价。原来只比 ownLoss / enemyGain 两个原始数，等于白送这一层。
            const int NibiruSelfValue = 3000 + 800;
            int freedAtk = NibiruFreedAttack();
            int backToken = freedAtk + 800;
            int net = (enemyGain - backToken) - (ownLoss - NibiruSelfValue);
            if (enemyGain <= ownLoss || net <= 0)
            {
                Log("陨石 → 对方表侧资源 " + enemyGain + " / 我方要交出 " + ownLoss
                    + "（换回尼比鲁 " + NibiruSelfValue + "，对方的衍生物 " + backToken
                    + "）→ 净收益 " + net + " ≤ 0，解放自己场上的怪不划算 → 留着");
                return false;
            }

            _nibiruUsed = true;
            Log("陨石 发动（对方本回合已召唤/特招 " + _enemySummonCount
                + " 只；对方表侧资源 " + enemyGain + " > 我方 " + ownLoss
                + "，净收益 +" + net + "）→ 解放双方表侧怪兽");
            return true;
        }

        // 陨石结算时会被解放的**攻击力合计**（决定对方拿到的那只衍生物多大）。
        //   只算表侧（里侧不参与解放）；我方 EX 区大姐免疫，不算进去 —— 与 `NibiruSideValue` 同一口径。
        private int NibiruFreedAttack()
        {
            int total = 0;
            foreach (ClientCard c in Bot.GetMonsters())
            {
                if (c == null || !c.IsFaceup() || JackpotImmune(c)) continue;
                total += Math.Max(c.Attack, 0);
            }
            foreach (ClientCard c in Enemy.GetMonsters())
            {
                if (c == null || !c.IsFaceup()) continue;
                total += Math.Max(c.Attack, 0);
            }
            return total;
        }

        // 陨石「会被解放的那一侧」的场面资源估值（mine=true → 我方会损失多少）。
        //   只算表侧怪兽（里侧不会被解放）；我方 EX 区大姐免疫，不算损失。
        //   权重：打点为主；额外卡组的怪（自带压制 / 素材）+1200；每张超量素材 +600。
        private int NibiruSideValue(bool mine)
        {
            IEnumerable<ClientCard> monsters = mine ? Bot.GetMonsters() : Enemy.GetMonsters();
            int total = 0;
            foreach (ClientCard c in monsters)
            {
                if (c == null || !c.IsFaceup()) continue;
                if (mine && JackpotImmune(c)) continue;
                total += Math.Max(c.Attack, 0) + 800;
                if (Bot.ExtraDeck.Any(x => x != null && x.Id == c.Id)) total += 1200;
                total += c.Overlays.Count * 600;
            }
            return total;
        }

        // 效果遮蒙者（97268402）：从手牌丢弃，指定对方 1 只**场上的表侧怪兽**，本回合它的效果无效。
        // ★ v5.0：必须瞄准「刚刚发动效果的那只怪」。
        //   旧版只判断「对方场上有攻击力 ≥1800 的表侧怪」就发，然后把选目标这一步交给了默认逻辑 ——
        //   默认逻辑按 hint 乱挑，抓了只无关的杂鱼，等于白发一张手坑（对方那只怪的效果照样处理完）。
        //   现在：连锁上最后一张卡（= 对方刚发动的卡）必须**就在场上、表侧、是怪兽**，
        //   才交遮蒙者，并且目标锁死是它；凑不上就不发，留着。
        private bool UseVeiler()
        {
            // ★ v5.1：自由时点（连锁为空）→ 走「提前无效」分支。
            //   对方场上有「把自身解放 / 送去墓地 / 除外 才能发动」的怪（救援猫、救援兔一类）时，
            //   它发动效果的那一刻自己已经离场了，而遮蒙者只能指「场上的表侧怪兽」——
            //   连锁它等于无对象可指，永远拦不住。唯一能生效的窗口就是「它还在场上、还没发动」。
            if (!InChain()) return VeilerPreemptive();

            ClientCard last = Util.GetLastChainCard();
            if (last == null || last.Controller != 1) return false;

            // ① 遮蒙者只能指「场上的表侧怪兽」。对方从手牌 / 墓地 / 魔陷区发动的一律指不到。
            bool targetable = last.IsOnField() && last.Location == CardLocation.MonsterZone
                && last.IsFaceup() && last.HasType(CardType.Monster);
            if (!targetable)
            {
                Log("手坑 效果遮蒙者 → 发动的是「" + (last.Name ?? "?") + "」，不在怪兽区/不是表侧怪兽，遮蒙者指不到 → 留着");
                return false;
            }

            // ② 已经无效化的怪就不必再指（指了也没收益）
            if (last.IsDisabled())
            {
                Log("手坑 效果遮蒙者 → 对方「" + last.Name + "」已被无效 → 留着");
                return false;
            }

            // ★ v5.28 用户口径：「如果对方场上发动的检索的怪兽效果，应该优先让大姐置换」——
            //   replay 23:42:26 对方特招「疾行机人 贝陀螺集合体」发动 ②（从卡组加 1 张）时，
            //   AI 把遮蒙者交了出去，于是这条链被彻底无效，大姐 23:42:35 只能打
            //   「替换检索 → 对方这条检索已被我方无效，替换无从生效 → 不发」；
            //   而 23:42:42 另一次检索大姐替换成功，对方手牌有 6 张 —— 正是「随机抽 1 不痛」的场合。
            //   ⇒ 判据与 `AshYieldToJackpot` 同源（EX 区大姐在场 + 对方手牌 >3），
            //     多一条更严的限定：这条链的效果必须是「从卡组加入手卡」，否则大姐 e5 根本不会触发
            //     （它的触发类别是 `CATEGORY_SEARCH|DRAW`），让位就成了纯亏。
            //   遮蒙者只是「本回合这只怪的效果无效」，本身没有一回合一次的硬限制，
            //   让掉这一手仍然留着给对面后面的动作，不浪费。
            if (JackpotWillReplaceSearch(last) && Enemy.Hand.Count > 3)
            {
                Log("效果遮蒙者 → 让给大姐置换（对方手牌 " + Enemy.Hand.Count
                    + " 张，这条检索会被改成随机抽 1 不痛）→ 遮蒙者留给后面");
                return false;
            }

            if (!UseNegate("效果遮蒙者")) return false;
            _veilerTarget = last;                 // 目标锁死：就是刚发动效果的那只
            return true;
        }

        // ★ v5.28：EX 区大姐此刻能不能把「这条链的检索」改成「对方随机抽 1」。
        //   两个前提缺一不可：
        //   · 大姐在 EX 区、且自己没被无效（`IsDisabled`）—— 被无效就改不动效果；
        //   · 链上这张卡的效果是「从卡组加入手卡」（`RaiseMoonScriptWipe.TakesFromDeckToHand`）——
        //     大姐 e5 的触发类别是 `CATEGORY_SEARCH|DRAW`，别的效果（墓地回收、手牌特招…）
        //     引擎压根不会问它，让位给一个不会来的替换就是白送对方一次精准检索。
        private bool JackpotWillReplaceSearch(ClientCard last)
        {
            if (last == null) return false;
            if (!Bot.GetMonstersInExtraZone().Any(c => c != null && c.IsCode(CardId.Jackpot) && !c.IsDisabled()))
                return false;
            return RaiseMoonScriptWipe.TakesFromDeckToHand(last.Id);
        }

        // ===== 遮蒙者「提前无效」：专治 cost 离场的起动效果 =====
        // 规则前提（卡文）：遮蒙者「对方主要阶段，把这张卡从手卡送去墓地，
        //   以对方场上 1 只效果怪兽为对象才能发动」——所以只能在对方主阶、只能指场上的表侧怪。
        // ⇒ 救援猫（把场上的这张卡送去墓地才能发动）、救援兔（把场上的这张卡除外才能发动）
        //   这类怪的效果一上链，它自己已经不在场上了，遮蒙者连锁时找不到合法对象。
        //   必须在它「还站在场上、但还没发动」这段窗口里先交。
        //
        // 时点：对方主阶里我方拿到自由时点（连锁为空）时。典型是
        //   · 对方进入主阶 1 的阶段转换响应；
        //   · 对方在主阶做完别的动作后让过优先权；
        //   · 对方宣言进入战斗阶段前的响应窗口。
        private bool VeilerPreemptive()
        {
            if (Duel.Player != 1) return false;                       // 遮蒙者只能对方回合发
            if (Duel.Phase != DuelPhase.Main1 && Duel.Phase != DuelPhase.Main2) return false;

            ClientCard target = PickCostLeaveFieldTarget();
            if (target == null) return false;

            _veilerTarget = target;
            Log("手坑 效果遮蒙者 → 提前无效「" + target.Name
                + "」（cost 离场型效果，等它发动就指不到了）");
            return true;
        }

        // 在对方场上挑一只「cost 离场才能发动」的表侧怪。
        // 优先「本回合刚摆上场」的那只：刚通召/特召上来就说明这回合准备动它。
        private ClientCard PickCostLeaveFieldTarget()
        {
            List<ClientCard> cands = Enemy.GetMonsters()
                .Where(c => c != null && c.IsFaceup() && !c.IsDisabled() && HasCostLeaveFieldEffect(c))
                .ToList();
            if (cands.Count == 0) return null;

            if (_enemyLastSummonedTurn == Duel.Turn)
            {
                ClientCard recent = cands.FirstOrDefault(c => c.Id == _enemyLastSummonedId);
                if (recent != null) return recent;
            }
            return cands.OrderByDescending(c => c.Attack).First();
        }

        // 「把（场上的）这张卡 / 自己 解放·送去墓地·除外·回到卡组/手牌 …… 发动」
        // 只认「自身作为 cost 离场」这一种；从手卡丢弃（手坑）、从墓地除外（墓地效果）
        // 都不属于要提前防的范围，靠 match 段里出现「手卡/手牌/墓地/卡组」剔掉。
        private static readonly Regex CostLeavePattern = new Regex(
            "把[^。；，]{0,8}(?:这张卡|自己)[^。；，]{0,3}(?:解放|送去墓地|除外|回到卡组|回到手牌)[^。；，]{0,15}发动",
            RegexOptions.Compiled);

        private static bool HasCostLeaveFieldEffect(ClientCard c)
        {
            if (c == null || !c.HasType(CardType.Monster)) return false;
            string desc = c.Data?.Description;
            if (string.IsNullOrEmpty(desc)) return false;
            if (desc.IndexOf('\n') >= 0 || desc.IndexOf('\r') >= 0)
                desc = desc.Replace("\r", "").Replace("\n", " ");

            foreach (Match m in CostLeavePattern.Matches(desc))
            {
                string seg = m.Value;
                if (seg.Contains("手卡") || seg.Contains("手牌")) continue;        // 手坑：从手卡丢弃
                if (seg.Contains("从墓地") || seg.Contains("墓地的")) continue;   // 墓地效果
                if (seg.Contains("从卡组") || seg.Contains("卡组的")) continue;   // 从卡组送墓之类
                return true;
            }
            return false;
        }

        // ===== 手坑盖伏苟命（v3.8）=====
        // 用户口径：手里一张本家怪都没抽到（完全展不开），对方场上的怪兽已经够直接打死自己
        //   → 从手牌拿一张手坑怪兽**盖到怪兽区**顶一下。
        //
        // 为什么盖怪能苟命：盖伏（守备表示）的怪兽被直接攻击时，只有「守备力低于对方攻击力 → 战破」，
        //   本体本身**不受战斗伤害**（贯穿除外）。⇒ 一只盖怪 = 挡掉一次直接攻击。
        //   所以需要的盖卡数 = 按攻击力从高到低削，直到剩下的场攻打不死我为止。
        //
        // ★ 只在我方**没有攻击表示怪兽**时启用：有攻击表示怪的话，对方会先打那只，
        //   伤害变成差值计算，这里算不准；而且场上还站着怪也不到「被打死」的地步。
        private bool SetHandTrapAsShield()
        {
            // ★ v4.4 / v5.40：水母停铺 → 连「盖手坑」也不做：里侧盖怪属于通常召唤（从手牌 Set），
            //   同样会触发水母让对面抽 1。大姐已在场时不缺这一只挡刀。
            if (_jellyfishFreeze)
            {
                Log("手坑 水母停铺 → 不盖当盾（盖怪=通常召唤，会喂水母）" + JellyfishFreezeWhy());
                return false;
            }
            if (Card == null || Duel.Player != 0) return false;
            int myRank = ShieldTrapRank(Card);
            if (myRank < 0) return false;                                  // 只盖手坑
            if (Bot.LifePoints <= 0) return false;
            if (Bot.GetMonsters().Count >= 7) return false;                // 怪兽区 + EX 区都满了

            // 手里还有本家怪兽 → 还能展开，不拿手坑去挡
            if (Bot.Hand.Any(c => c != null && c.HasSetcode(0x1ec) && c.HasType(CardType.Monster)))
                return false;
            // 我方有攻击表示的怪兽 → 对方不会直接打脸
            if (Bot.GetMonsters().Any(c => c.IsFaceup() && c.IsAttack())) return false;

            List<ClientCard> attackers = Enemy.GetMonsters()
                .Where(c => c != null && c.IsFaceup() && c.IsAttack() && c.Attack > 0).ToList();
            if (attackers.Count == 0) return false;

            int incoming = attackers.Sum(c => c.Attack);
            if (incoming < Bot.LifePoints) return false;                   // 打不死 → 手坑留着干扰更值

            // 需要挡几次：从最高攻开始削，剩下的场攻还够打死我才继续盖
            int need = 0;
            int remain = incoming;
            foreach (int atk in attackers.Select(c => c.Attack).OrderByDescending(a => a))
            {
                if (remain < Bot.LifePoints) break;
                remain -= atk;
                need++;
            }

            int shields = Bot.GetMonsters().Count(c => c.IsDefense());
            if (shields >= need) return false;

            // 手里有多张手坑时只让「最不心疼」的那张出手，免得把灰流丽 / G 先盖了
            int best = Bot.Hand.Where(c => c != null && ShieldTrapRank(c) >= 0)
                .Select(c => ShieldTrapRank(c)).DefaultIfEmpty(-1).Min();
            if (myRank != best) return false;

            Log("没本家怪可展开 + 对方场攻 " + incoming + " ≥ 我 LP " + Bot.LifePoints
                + " → 盖 " + Card.Name + " 挡刀（已挡 " + shields + "/" + need + "）");
            return true;
        }

        // 盖哪张最不心疼：锁鸟（先攻局本来就留着不丢）< 遮蒙者 < 幽鬼兔 < 灰流丽 < G（抽卡收益最高）
        private int ShieldTrapRank(ClientCard c)
        {
            if (c == null) return -1;
            if (c.IsCode(CardId.LockBird)) return 0;
            if (c.IsCode(CardId.Veiler)) return 1;
            if (c.IsCode(CardId.GhostRabbit)) return 2;
            if (c.IsCode(CardId.AshBlossom)) return 3;
            if (c.IsCode(CardId.MaxxC)) return 4;
            return -1;
        }

        // 单点干扰（灰流丽 / 幽鬼兔 / 遮蒙者）的统一闸门：
        //   ① 只在对方回合、对方在发动效果时交（绝不对着自己的效果交）；
        //   ② 同一条连锁里最多交 1 张 —— 判据是「这条连锁上已经躺着一张我方手坑」。
        //      ★ 不能用「上一张手坑发过后置个标记，等下条连锁再清」那种写法：
        //        下一条连锁建立时 CurrentChain.Count 已经 ≥ 1，永远等不到清零的那一瞬，
        //        标记会卡死、从此一张手坑都不交。而连锁上有没有我方手坑是**可观测**的，
        //        每个连锁天然独立，不需要任何状态。
        private bool UseNegate(string name, bool anyTurn = false)
        {
            // ★ v5.21：`anyTurn` 只给灰流丽用 —— 她的卡文不区分回合，对方在我方回合发动
            //   「从卡组把卡加入手卡」的效果同样要吃无效。真正该守的是「链上是对方的卡」，
            //   那条由 `EnemyActivated()` 保证，比「看谁的回合」精确。
            //   幽鬼兔 / 遮蒙者仍维持只在对方回合交（默认 false），不动它们。
            if (!anyTurn && Duel.Player != 1) return false;
            if (!EnemyActivated()) return false;
            if (NegateAlreadyInChain())
            {
                Log("手坑 " + name + " 本连锁已交过 → 留着");
                return false;
            }
            Log("手坑 " + name + " 发动");
            return true;
        }

        private bool NegateAlreadyInChain()
        {
            if (Duel.CurrentChain == null) return false;
            foreach (ClientCard c in Duel.CurrentChain)
            {
                if (c == null || c.Controller != 0) continue;
                if (c.IsCode(CardId.AshBlossom) || c.IsCode(CardId.GhostRabbit) || c.IsCode(CardId.Veiler))
                    return true;
            }
            return false;
        }

        // ★ v5.26：这条连锁上正在结算的**对方效果**，是否已经被我方「无效」掉了？
        //   只认两张真正让效果作废的：
        //     · 灰流丽（AshBlossom）：无效并除外 —— 她只能对「从卡组加手卡 / 从卡组特招 / 从卡组送墓」发，
        //       与大姐 e5 关心的 `CATEGORY_SEARCH` 完全重合，交了就必然让这条检索作废；
        //     · 遮蒙者（Veiler）：把那只怪兽的效果无效 —— `UseVeiler` 只会指向链上最后一环那只表侧怪兽，
        //       所以链上出现它就等于那条怪兽效果已作废。
        //   **幽鬼兔不算**：它只是把那只怪**破坏**，已经发动的效果照常结算（v5.24 曾把它算进来，是错的）。
        //
        //   这个判据只服务于「替换有没有意义」，与 `NegateAlreadyInChain()`（手坑去重）是两回事，
        //   也和 v5.25 的口径（有手坑不禁替换）不冲突：
        //     「有手坑」≠「原效果被无效」，只有后者才让替换无从生效。
        private bool ChainSearchNegatedByUs()
        {
            if (Duel.CurrentChain == null) return false;
            foreach (ClientCard c in Duel.CurrentChain)
            {
                if (c == null || c.Controller != 0) continue;
                if (c.IsCode(CardId.AshBlossom) || c.IsCode(CardId.Veiler)) return true;
            }
            return false;
        }

        // 素材优先级：先给对本回合已经没有后续价值的，把「翼」留到最后（它要留在场上开 ③）
        private static int MaterialRank(ClientCard c)
        {
            if (c.IsCode(CardId.Carol)) return 0;
            if (c.IsCode(CardId.Squiz)) return 1;
            if (c.IsCode(CardId.Hiena) || c.IsCode(CardId.Kurusu)) return 2;
            if (c.IsCode(CardId.Siero)) return 3;
            if (c.IsCode(CardId.Rabi)) return 9;
            return 5;
        }

        // 引擎送来的候选列表里找回「发动时算好的那张卡」。
        // 优先按对象引用比，比不到再按「同卡号 + 同控制者」兜底（引擎可能重建了 ClientCard 实例）。
        private static ClientCard MatchTarget(IList<ClientCard> cards, ClientCard target)
        {
            if (target == null) return null;
            return cards.FirstOrDefault(c => c == target)
                ?? cards.FirstOrDefault(c => c.Id == target.Id && c.Controller == target.Controller);
        }

        // ★ v5.15 用户口径（优化16-3）：「场上如果有多余的本家 7 星，叠超量时素材最好**别用**
        //   西艾萝 / 菈碧 —— 帐要在对方主要阶段抽 1，翼要在对方回合 ③ 加速超量，都是站场才生效的；
        //   怪不够时照常拿它们当素材（该叠还得叠，不能为了省一只把整个展开停下来）。」
        //   优先级：先甩「本回合已经没有后续价值」的（雅 / 朔 / 希耶娜 / 库露雪，见 MaterialRank），
        //   有余量就整只不碰 西艾萝 + 菈碧；只够保一只时保菈碧（③ 加速是实打实的干扰），
        //   连一只有余量都没有才轮到西艾萝 —— 顺序和 MaterialRank 里的 3 / 9 一致。
        private static IList<ClientCard> PickMaterials(IList<ClientCard> cards, int max)
        {
            List<ClientCard> usable = cards.Where(c => c.Level == 7).OrderBy(MaterialRank).ToList();
            int need = Math.Min(max, usable.Count);
            if (need < 2) need = Math.Min(2, usable.Count);

            // ① 一只西艾萝 / 菈碧都不动
            List<ClientCard> keepBoth = usable.Where(c => !c.IsCode(CardId.Siero) && !c.IsCode(CardId.Rabi)).ToList();
            if (keepBoth.Count >= need) return keepBoth.Take(need).ToList();

            // ② 只保住菈碧（素材里仍然不用它）
            List<ClientCard> keepRabi = usable.Where(c => !c.IsCode(CardId.Rabi)).ToList();
            if (keepRabi.Count >= need) return keepRabi.Take(need).ToList();

            // ③ 怪真的不够 → 正常拿
            return usable.Take(need).ToList();
        }

        // 群星置顶的候选表（每档只查这几个 Id，顺序 = 该档内部的偏好）
        private static readonly int[] StackOrder = { CardId.Carol, CardId.Rabi, CardId.Siero, CardId.Hiena, CardId.Kurusu };
        // ★ v4.4：水母 combo —— 优先置顶「对方回合能干扰」的本家：
        //   菈碧（③ 加速 R7）/ 希耶娜（弹对方怪兽）/ 库露雪（弹对方魔陷）/ 帐（③ 抽 1 续场）
        private static readonly int[] StackOrderCombo = { CardId.Rabi, CardId.Hiena, CardId.Kurusu, CardId.Siero, CardId.Carol };

        // ================= 群星 ① 置顶：挑哪张（v5.17 重写）=================
        //
        // ★ 用户口径（replay「2026-10-24 21-38-04.yrp」）：
        //   「如果手牌**或者场上**没有西艾萝，那么第一个群星应该置顶西艾萝（西艾萝抽卡效果一回合只能发动一次），
        //     其他情况则是看情况置顶：
        //     ① 我方需要抽卡 → 置顶卡萝尔；
        //     ② 我方需要破坏对方场上的怪兽 → 置顶希耶娜；
        //     ③ 我方要破坏对方场上的魔法陷阱卡 → 置顶库露雪；
        //     ④ 我方场上已经有 2 张 7 星本家或者大姐，这时最好置顶菈碧，
        //        以达成在对方回合加速超量干扰的效果。」
        //
        // v5.15 / v5.16 的病根：只查 `Bot.Hand`（手牌），没查场上。
        //   录像 21:38 那一手：21:38:10 与 21:38:15 两只帐已经用 ② 返手卡自跳站到场上，
        //   手里当然没有帐 —— 21:38:21 群星却把**第三只帐**置了顶。
        //   帐 ③ 是「1 回合 1 次」的，第三只帐站上来只会是一个 2100 白板，
        //   既不能多抽一张，也顶不掉别人的位置。
        //
        // 判定顺序（从强到弱，命中即返回）：
        //   ⓿ 手牌 + 场上都没有西艾萝 → 西艾萝（持续抽卡源，最优先）；
        //   ④ 场上已有 2 只 7 星本家 或 大姐 → 菈碧（抽到自跳后，对方回合 ③ 加速超量做干扰）；
        //   ② 对方前场有 2000+ 的表侧怪 → 希耶娜（抽到自跳 → ② 弹回卡组）；
        //   ③ 对方后场 / 场地有魔陷 → 库露雪（抽到自跳 → ② 弹回那张魔陷）；
        //   ① 默认（都没有具体目标，等于「就是要抽卡」）→ 卡萝尔（抽到自跳 + ③ 再抽 1）。
        // 为什么 ④ 排在 ②③ 前面：④ 的条件是「我方场面已经成型」，这时对方回合那一次加速
        //   是**多出来的一次**超量（翼 ③ 不占自己回合的动作），收益高于「弹对方 1 张」；
        //   而 ①②③ 是在场面没成型时，靠抽到哪张来补不同的缺口。
        // 为什么 ① 排在 ②③ 后面：卡萝尔是通用兜底 —— 一旦把它排前面，
        //   对方场上有明确的怪 / 魔陷要弹时也永远轮不到希耶娜 / 库露雪（「需要抽卡」永远成立）。
        private ClientCard PickStackFrom(IList<ClientCard> deck, HashSet<int> used)
        {
            // 水母 combo：整表让位（它要的是「对方回合能干扰」的那几张，顺序已经排好）
            if (_jellyfishCombo)
            {
                // ★ v5.40：按对方场上「有没有东西可弹」来定置顶谁 —— 置顶的那张要靠对方回合
                //   「大姐另开连锁抽 1 / 二速抽卡」抽出来自跳（① 被抽到时自跳），落地就得有用：
                //     · 对方有表侧魔陷 / 场地 → 库露雪（② 弹魔陷）；
                //     · 对方有 2000+ 的表侧怪 → 希耶娜（② 弹怪兽）；
                //     · 都没有 → 菈碧（③ 对方回合加速超量，做干扰终端）。
                int[] order;
                string why;
                if (Enemy.GetSpells().Any(c => c != null && c.IsFaceup()))
                {
                    order = new[] { CardId.Kurusu, CardId.Rabi, CardId.Hiena, CardId.Siero, CardId.Carol };
                    why = "对方有表侧魔陷 → 库露雪（② 弹魔陷）";
                }
                else if (Enemy.GetMonsters().Any(c => c != null && c.IsFaceup() && c.Attack >= 2000))
                {
                    order = new[] { CardId.Hiena, CardId.Rabi, CardId.Kurusu, CardId.Siero, CardId.Carol };
                    why = "对方有 2000+ 的表侧怪 → 希耶娜（② 弹怪兽）";
                }
                else
                {
                    order = StackOrderCombo;
                    why = "对方没有明确的弹卡目标 → 菈碧（③ 对方回合加速超量）";
                }
                ClientCard comboPick = PickFirstStack(deck, used, order);
                if (comboPick == null) return FallbackStack(deck, used);
                _comboTopHeld = true;   // ★ 本回合剩下的抽卡让位，这张留给对方回合大姐抽 1
                Log("群星 置顶理由：水母 combo " + why + " → 本回合不再抽走（留给对方回合大姐抽 1 抽出来自跳）");
                return comboPick;
            }

            // ⓿ 手牌 + 场上都没有西艾萝
            bool sieroInHand = Bot.Hand.Any(c => c != null && c.IsCode(CardId.Siero));
            bool sieroOnField = Bot.GetMonsters().Any(c => c != null && c.IsCode(CardId.Siero));
            if (!sieroInHand && !sieroOnField)
            {
                ClientCard siero = PickFirstStack(deck, used, new[] { CardId.Siero });
                if (siero != null)
                {
                    Log("群星 置顶理由：手牌和场上都没有西艾萝 → 先立持续抽卡源");
                    return siero;
                }
            }

            // ④ 场上已有 2 只 7 星本家 或 大姐 → 菈碧（对方回合加速超量）
            int l7 = Bot.GetMonstersInMainZone()
                .Count(c => c != null && c.IsFaceup() && c.Level == 7 && !c.HasType(CardType.Xyz));
            bool jackpot = Bot.GetMonsters().Any(c => c != null && c.IsCode(CardId.Jackpot));
            if (l7 >= 2 || jackpot)
            {
                ClientCard rabi = PickFirstStack(deck, used, new[] { CardId.Rabi });
                if (rabi != null)
                {
                    Log("群星 置顶理由：场上 7 星 " + l7 + " 只" + (jackpot ? " + 大姐" : "")
                        + " → 置顶菈碧（对方回合 ③ 加速超量做干扰）");
                    return rabi;
                }
            }

            // ② 对方前场有站得住的大怪 → 希耶娜（被抽到自跳 → ② 弹回卡组）
            if (Enemy.GetMonsters().Any(c => c != null && c.IsFaceup() && c.Attack >= 2000))
            {
                ClientCard hiena = PickFirstStack(deck, used, new[] { CardId.Hiena });
                if (hiena != null)
                {
                    Log("群星 置顶理由：对方前场有 2000+ 的怪 → 置顶希耶娜（弹回卡组）");
                    return hiena;
                }
            }

            // ③ 对方后场 / 场地有魔陷 → 库露雪（被抽到自跳 → ② 弹回那张魔陷）
            int enemySt = Enemy.GetSpells().Count(c => c != null);
            if (enemySt > 0)
            {
                ClientCard kurusu = PickFirstStack(deck, used, new[] { CardId.Kurusu });
                if (kurusu != null)
                {
                    Log("群星 置顶理由：对方后场有 " + enemySt + " 张魔陷 → 置顶库露雪（弹回卡组）");
                    return kurusu;
                }
            }

            // ① 默认（＝就是要抽卡）→ 卡萝尔
            ClientCard carol = PickFirstStack(deck, used, new[] { CardId.Carol });
            if (carol != null)
            {
                Log("群星 置顶理由：没有具体要拆的目标，默认需要抽卡 → 置顶卡萝尔");
                return carol;
            }

            return PickFirstStack(deck, used, StackOrder) ?? FallbackStack(deck, used);
        }

        // 按给定顺序在卡组里挑第一张（跳过本回合已经置顶过的同名卡）
        private static ClientCard PickFirstStack(IList<ClientCard> deck, HashSet<int> used, int[] order)
        {
            foreach (int id in order)
            {
                if (used != null && used.Contains(id)) continue;
                ClientCard c = deck.FirstOrDefault(x => x != null && x.IsCode(id));
                if (c != null) return c;
            }
            return null;
        }

        // 兜底：优先本家 7 星，最后随便一张没置顶过的
        private static ClientCard FallbackStack(IList<ClientCard> deck, HashSet<int> used)
        {
            return deck.FirstOrDefault(x => x != null && (used == null || !used.Contains(x.Id)))
                ?? deck.FirstOrDefault(x => x != null && ArchetypeL7.Contains(x.Id))
                ?? deck.FirstOrDefault();
        }

        private int HandCardValue(ClientCard c)
        {
            // 越低越先被送回卡组。
            //
            // ★ v4.6 用户口径（三档，从高到低 ＝ 越靠前越先被洗回卡组）：
            //     菈碧 ＞ 手坑 ＞ 菈碧以外的本家卡
            //   （v4.5 及以前是「手坑 ＞ 一切本家」，用户 10:58 补充：菈碧要排在手坑前面。）
            //
            //   ① 菈碧排第一：手牌里的菈碧是**纯废牌**。
            //      它是 Lv7，通召要 2 个祭品（通召不动）；①「被抽到时自跳」只有从**卡组被抽到**才触发，
            //      躺在手里既不触发也不能上场、不能当素材 —— 洗回卡组反而才是它该待的地方
            //      （之后靠群星置顶 / 抽卡再摸回来就能立刻自跳）。
            //   ② 手坑第二：本来就要等到对方回合才有用，洗回去不影响本回合，靠满编抽卡还能再摸回来。
            //   ③ 其余本家最后：希耶娜 / 库露雪 / 西艾罗 / 卡萝尔 / 朔 在手里是「丢手抽 1」的引擎，
            //      洗掉就是自断展开。
            if (c.IsCode(CardId.Rabi)) return 1;          // ★ 最高优先级：手牌菈碧 = 废牌，最想让它回卡组
            //
            // ★ v4.6 手坑内部也要分先后（同档第二梯位）。
            //   「回掉」的优先级从低到高（＝越靠前越先被洗回卡组）：
            //     陨石 ＜ 增殖的G ＜ 锁鸟 ＜ 灰流丽 ＜ 幽鬼兔 ＜ 效果遮蒙者
            //   即：遮蒙者最先走（本卡组 ×3、对方回合干扰面最窄），
            //       幽鬼兔 → 灰流丽 → 锁鸟 → 增殖的G → 陨石 依次，陨石最舍不得。
            //   ⇒ 分值越小越先回，且全部 < 15（未眠之城），保证「手坑排在未眠 / 其余本家之前」。
            if (c.IsCode(CardId.Veiler)) return 4;        // 效果遮蒙者 ×3：最先可以舍弃
            if (c.IsCode(CardId.GhostRabbit)) return 5;   // 幽鬼兔 ×3：只炸发动效果的场上卡
            if (c.IsCode(CardId.AshBlossom)) return 6;    // 灰流丽 ×2：挡检索/卡组特招/堆墓
            if (c.IsCode(CardId.LockBird)) return 7;      // 锁鸟 ×1：对方检索后的封锁
            if (c.IsCode(CardId.MaxxC)) return 8;         // 增殖的G ×1：强手坑，很靠后才考虑洗回
            // ★ v5.13（replay _LastReplay 16:02:10）：**陨石一直被漏掉** —— 它不在任何一档里，
            //   于是落进方法末尾 `return 30` 的兜底，比未眠之城（15）还晚洗回：
            //   那一手手牌同时有「陨石 + 未眠之城」，结果把场地洗回去了、把陨石留在手里。
            //   陨石是单张、又是「对方铺满之后唯一的全场清盘」→ 手坑里最舍不得的一张，给 9。
            if (c.IsCode(CardId.Nibiru)) return 9;        // 原始生命态 尼比鲁 ×1
            // ★ v5.12 用户口径（replay/优化12）：手卡返卡组的优先级**和西艾罗② 完全一致**，从高到低是
            //     菈碧 ＞ 手坑 ＞ **未眠之城** ＞ 本家其他怪兽 ＞ 群星 ＞ 辉煌
            //   （＝ 数值越小越先被洗回去）。所以未眠之城从 v5.9 的 70 提到 15，紧跟在手坑后面：
            //   未眠② 是**场上那张永续魔法**发动的效果，手里摸到的第二张同名卡只能自己再铺一张
            //   （同名卡在魔陷区可以各占一格，但② 一回合只有 1 次）—— 比本家怪兽（丢手抽卡 /
            //   返卡自跳的引擎）更该先走。
            if (c.IsCode(CardId.City)) return 15;         // 未眠之城：菈碧 / 手坑之后，本家怪兽之前
            // ★ v5.9 用户口径（replay/优化10）：**陷阱辉煌是全场最不该被洗回的牌**。
            //   「升月这个卡组陷阱的优先级很高，应该留着 —— 陷阱可以对方回合拉大姐来形成干扰。」
            //   盖下去就是：对方回合 ① 从额外拉大姐（EX 区封锁 + 检索替换）、② 墓地除外抽 1。
            //   所以从原来的 15（最先洗回）改到 80（最后才动），手牌满到要洗牌时先动场地 / 本家。
            if (c.IsCode(CardId.Splendor)) return 80;
            // ★ v5.22 用户口径：**卡萝尔是最后才动的一张牌** —— 她是本卡组难得的通召点
            //   （② 不用解放就能通召，③ 通召/特召成功时抽 1），洗回卡组等于白扔一个召唤权。
            //   只有手牌满到别的都洗不动时才会轮到她（分 90，比辉煌的 80 还靠后）。
            if (c.IsCode(CardId.Carol)) return 90;
            if (c.IsCode(CardId.Burial)) return 20;       // 副葬是启动点，比废牌值钱
            if (ArchetypeL7.Contains(c.Id)) return 60;    // 其余本家：留在手里等丢手抽卡
            if (c.IsCode(CardId.Stars)) return 70;        // 群星：置顶/墓地抽 1，比本家更值得留
            return 30;
        }

        private string CardName()
        {
            return Card == null ? "?" : Card.Name;
        }

        private static string Location(ClientCard c)
        {
            return c == null ? "?" : c.Location.ToString();
        }

        // 调试日志：写到 WindBot\log\shengyue.log，方便复盘「为什么没发动」
        private static readonly object _logLock = new object();

        private static void Log(string msg)
        {
            try
            {
                lock (_logLock)
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "log");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "shengyue.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志失败绝不影响对局
            }
        }
    }
}
