using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WindBot.Game.AI.Decks
{
    // 「不取对象的群体清场」检测：直接读这张卡的 lua 脚本，判断它会不会一次把整个区域拆掉。
    //
    // 为什么必须自己做这件事：
    //   取对象的效果（旋风 / 强脱 / 次元幽闭）在以我们为目标时，服务端会发 MSG_BECOME_TARGET，
    //   进 Duel.ChainTargets，用 Util.IsChainTarget 就能提前知道；
    //   但「破坏场上所有卡」这类**不取对象**的清场根本不发这个包 —— ChainTargets 是空的，
    //   卡被拆掉之前一点信号都没有。只能反过来问：「现在连锁上的这张卡，会不会清场？」
    //
    // 判据（逐函数体，只看真正喂给 Duel.Destroy / Remove / Sendto* 的那个组）：
    //   1. 函数体里没有 Duel.SelectTarget —— 有就是取对象，另有路径覆盖，不算清场
    //   2. Duel.Destroy / Remove / SendtoGrave / SendtoDeck / SendtoHand 的实参是个组，
    //      且该组来自 local g = Duel.GetFieldGroup / GetMatchingGroup(...)
    //   3. 区域：LOCATION_ONFIELD / LOCATION_ALL → 怪兽+魔陷；LOCATION_MZONE → 怪兽；LOCATION_SZONE → 魔陷
    //      若组带过滤函数（例：羽毛扫的 filter 限定 TYPE_SPELL+TYPE_TRAP），以过滤函数为准
    //   4. 落点：组取函数的 (自己, 对手) 两个区域参数里，对手那一格是 0 → 只拆自己的场，
    //      威胁不到我们，直接不算（这条砍掉约 1/4 的「自爆型」误报）
    //
    // 实测分类（样本见施工单）：
    //   黑洞 / 雷击 / 激流葬 / 圣防 → 怪兽
    //   大岚 / 羽毛扫             → 魔陷
    //   天霆号 阿宙斯              → 全场
    //   旋风 / 强脱 / 次元幽闭 / 双龙卷 / 沙尘 → 0（取对象，走 ChainTargets）
    internal static class RaiseMoonScriptWipe
    {
        public const int None = 0;
        public const int Monster = 1;     // 会拆怪兽区
        public const int SpellTrap = 2;   // 会拆魔陷区

        // 打包：低 2 位 = 拆「发动者自己的场」，高 2 位 = 拆「发动者的对手的场」
        private const int ShiftOpp = 2;

        private static readonly Dictionary<int, int> Cache = new Dictionary<int, int>();
        private static string _scriptDir;
        private static bool _dirResolved;

        private static readonly Regex AssignRx = new Regex(
            @"local\s+(\w+)\s*=\s*(Duel\.Get\w+\((?:[^()]|\([^()]*\))*\))",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // 会让卡「离开场上」的操作：破坏 / 除外 / 送墓 / 回卡组 / 回手牌
        private static readonly Regex LeaveRx = new Regex(
            @"Duel\.(?:Destroy|Remove|SendtoGrave|SendtoDeck|SendtoHand)\s*\(\s*([\w:\.]+)",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex FirstArgRx = new Regex(
            @"Duel\.Get\w+\(\s*([\w\.]+)\s*,", RegexOptions.Compiled);

        private static readonly Regex CallRx = new Regex(
            @"^Duel\.Get\w+\((.*)\)$", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex FuncRx = new Regex(
            @"function\s+([\w\.]+)\s*\([^)]*\)(.*?)\nend", RegexOptions.Singleline | RegexOptions.Compiled);

        // 「不会被效果破坏」类效果的注册码。全部是**自身**破坏抗性，跟幽鬼兔要对的那一下直接相关：
        //   EFFECT_INDESTRUCTABLE_EFFECT  —— 不会被效果破坏
        //   EFFECT_INDESTRUCTABLE_COUNT   —— 不会被破坏 N 次
        //   EFFECT_INDESTRUCTABLE         —— 旧式（不带后缀）= 战斗 + 效果都免
        //   EFFECT_DESTROY_SUBSTITUTE     —— 可以用别的卡代替破坏
        //   EFFECT_DESTROY_REPLACE        —— 代替破坏（黑智天至那种「拔一个素材代替」）
        // ★ 明确排除 EFFECT_INDESTRUCTABLE_BATTLE（只免战斗破坏，效果破坏照样吃，不影响幽鬼兔）。
        //   `(?![_\w])` 这条负向前瞻就是干这个的：`_BATTLE` 里 `EFFECT_INDESTRUCTABLE` 后面紧跟下划线，匹配失败。
        private static readonly Regex DestroyImmuneRx = new Regex(
            @"EFFECT_INDESTRUCTABLE_EFFECT|EFFECT_INDESTRUCTABLE_COUNT|EFFECT_INDESTRUCTABLE(?![_\w])|EFFECT_DESTROY_SUBSTITUTE|EFFECT_DESTROY_REPLACE",
            RegexOptions.Compiled);

        private static readonly Dictionary<int, bool> ImmuneCache = new Dictionary<int, bool>();

        // 这张卡是不是「不会被效果破坏」—— 幽鬼兔的效果是「把发动效果的那张卡破坏」，
        // 砸在这种卡上等于白丢一张手牌（对方效果照常处理，卡还在场上），所以先问一句再决定发不发。
        public static bool CannotBeDestroyedByEffect(int cardId)
        {
            bool cached;
            if (ImmuneCache.TryGetValue(cardId, out cached)) return cached;

            bool res = false;
            try
            {
                string path = ScriptPath(cardId);
                if (path != null) res = DestroyImmuneRx.IsMatch(File.ReadAllText(path));
            }
            catch
            {
                res = false;   // 读不到就当「可以破坏」，保持原来的行为
            }

            ImmuneCache[cardId] = res;
            return res;
        }

        public static string ResolvedDir
        {
            get
            {
                ScriptPath(0);
                return _scriptDir;
            }
        }

        // ===== 「会不会被战斗破坏」=====
        // 重铠光子龙 ② 要拔 1 素材炸一张表侧卡 —— 炸**场地 / 永续**是纯赚，但炸「战破抗性怪」要看场合：
        //   ★ v5.27 用户口径：对方场上有这种怪时，宁可先降 500 打点（5300 → 4800）也要抢在战阶前炸掉它，
        //     否则战阶啃不动、还会被它继续干扰。
        // 判据只认「免战斗破坏」这一类：
        //   EFFECT_INDESTRUCTABLE_BATTLE   —— 不会被战斗破坏
        //   EFFECT_INDESTRUCTABLE(?![_\w]) —— 旧式（战斗 + 效果都免）
        //   EFFECT_INDESTRUCTABLE_COUNT    —— 不会被破坏 N 次（含战斗）
        //   EFFECT_BATTLE_DESTROY_REDIRECT —— 把战斗破坏转移给别人
        // ★ 与 `DestroyImmuneRx` 那一组恰好互补（那边明确排除 `_BATTLE`），别混用：
        //   幽鬼兔问的是「效果破坏能不能成」，重铠问的是「战阶能不能打死」。
        private static readonly Regex BattleImmuneRx = new Regex(
            @"EFFECT_INDESTRUCTABLE_BATTLE|EFFECT_INDESTRUCTABLE(?![_\w])|EFFECT_INDESTRUCTABLE_COUNT|EFFECT_BATTLE_DESTROY_REDIRECT",
            RegexOptions.Compiled);

        private static readonly Dictionary<int, bool> BattleImmuneCache = new Dictionary<int, bool>();

        public static bool CannotBeDestroyedByBattle(int cardId)
        {
            bool cached;
            if (BattleImmuneCache.TryGetValue(cardId, out cached)) return cached;

            // 读不到脚本 → 当「能战破」，保持原来的行为（不因为 DIY 卡缺脚本就乱抢节奏）。
            bool res = false;
            try
            {
                string path = ScriptPath(cardId);
                if (path != null) res = BattleImmuneRx.IsMatch(File.ReadAllText(path));
            }
            catch
            {
                res = false;
            }

            BattleImmuneCache[cardId] = res;
            return res;
        }

        // ===== 「会不会从卡组把卡加入手卡」=====
        // 朔·斯奎兹的「持续抽卡」（op=1）注册的是 EVENT_TO_HAND + 过滤「对方的卡从卡组加入手卡且不是抽卡」，
        // 只有对方真的**检索**才会触发。对方从卡组**堆墓**（愚蠢的副葬、各类送墓）根本不会触发，
        // 提前把它当 cost 丢掉就是白扔一张手牌。
        // AI 拿不到对方效果的 category，只能读它的 lua 脚本找痕迹：
        //   CATEGORY_SEARCH          —— 声明了「检索」类别
        //   HINTMSG_ATOHAND          —— 选择时要「加入手卡」
        //   Card.IsAbleToHand        —— 过滤条件里筛「能加入手卡」的卡（壶 / 检索魔法常这么写）
        //   SendtoHand + LOCATION_DECK —— 直接往手卡送，且脚本里动到卡组
        private static readonly Regex SearchRx = new Regex(
            @"CATEGORY_SEARCH|HINTMSG_ATOHAND|Card\.IsAbleToHand",
            RegexOptions.Compiled);

        private static readonly Dictionary<int, bool> SearchCache = new Dictionary<int, bool>();

        public static bool TakesFromDeckToHand(int cardId)
        {
            bool cached;
            if (SearchCache.TryGetValue(cardId, out cached)) return cached;

            // ★ 读不到脚本 → 按「可能是检索」处理，等于保持改动前的宽松行为。
            //   宁可多挂一次（多抽一张），也不要因为读不到 DIY 卡脚本就漏掉对方的检索。
            bool res = true;
            try
            {
                string path = ScriptPath(cardId);
                if (path != null)
                {
                    string t = File.ReadAllText(path);
                    res = SearchRx.IsMatch(t)
                        || (t.IndexOf("SendtoHand", StringComparison.Ordinal) >= 0
                            && t.IndexOf("LOCATION_DECK", StringComparison.Ordinal) >= 0);
                }
            }
            catch
            {
                res = true;
            }

            SearchCache[cardId] = res;
            return res;
        }

        // 这张卡（由 casterIsEnemy 一方发动）的效果，会不会把 want 指定的区域整个拆掉
        public static bool Wipes(int cardId, int want, bool casterIsEnemy)
        {
            int packed = PackedFlags(cardId);
            int mine = casterIsEnemy ? (packed >> ShiftOpp) : packed;
            return (mine & want) != 0;
        }

        // 调试用：返回「拆我方」的原始区域标记（0–3）
        public static int FlagsAgainstMe(int cardId, bool casterIsEnemy)
        {
            int packed = PackedFlags(cardId);
            return (casterIsEnemy ? (packed >> ShiftOpp) : packed) & 3;
        }

        private static int PackedFlags(int cardId)
        {
            int cached;
            if (Cache.TryGetValue(cardId, out cached)) return cached;

            int flags = None;
            try
            {
                string path = ScriptPath(cardId);
                if (path != null) flags = Classify(File.ReadAllText(path));
            }
            catch
            {
                flags = None;   // 读不到就当作「不是清场」，保持原有保守行为
            }

            Cache[cardId] = flags;
            return flags;
        }

        private static string ScriptPath(int cardId)
        {
            if (!_dirResolved)
            {
                _dirResolved = true;

                // 允许用环境变量直接指定（Bot 从别处启动 / 多份 ygopro 并存时用）
                string env = Environment.GetEnvironmentVariable("SHENGYUE_SCRIPT_DIR");
                if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
                {
                    _scriptDir = env;
                }
                else
                {
                    string b = AppDomain.CurrentDomain.BaseDirectory;
                    string[] candidates =
                    {
                        Path.Combine(b, "script"),
                        Path.Combine(Path.GetFullPath(Path.Combine(b, "..")), "script"),
                        Path.Combine(Path.GetFullPath(Path.Combine(b, "..", "..")), "script")
                    };
                    foreach (string c in candidates)
                    {
                        try
                        {
                            if (Directory.Exists(c))
                            {
                                _scriptDir = c;
                                break;
                            }
                        }
                        catch
                        {
                            // 路径非法就试下一个
                        }
                    }
                }
            }

            if (_scriptDir == null) return null;
            string p = Path.Combine(_scriptDir, "c" + cardId + ".lua");
            return File.Exists(p) ? p : null;
        }

        private static int Classify(string raw)
        {
            string text = raw.Replace("\r\n", "\n").Replace('\r', '\n');
            int flags = None;
            foreach (string body in Blocks(text))
                flags |= ClassifyBlock(body, text);
            return flags;
        }

        // 按 function 边界切块：一个块 = 一个效果函数
        private static IEnumerable<string> Blocks(string text)
        {
            List<string> cur = new List<string>();
            bool started = false;
            foreach (string raw in text.Split('\n'))
            {
                string s = raw.Trim();
                if (s.StartsWith("function ", StringComparison.Ordinal)
                    || s.StartsWith("local function ", StringComparison.Ordinal))
                {
                    if (started) yield return string.Join("\n", cur);
                    cur = new List<string>();
                    started = true;
                }
                cur.Add(s);
            }
            if (started) yield return string.Join("\n", cur);
        }

        private static int ClassifyBlock(string body, string whole)
        {
            if (body.IndexOf("Duel.SelectTarget", StringComparison.Ordinal) >= 0) return None;
            if (!LeaveRx.IsMatch(body)) return None;

            Dictionary<string, string> vars = new Dictionary<string, string>();
            foreach (Match m in AssignRx.Matches(body))
                vars[m.Groups[1].Value] = m.Groups[2].Value;

            int flags = None;
            foreach (Match m in LeaveRx.Matches(body))
            {
                string arg = m.Groups[1].Value;
                string expr;
                if (!vars.TryGetValue(arg, out expr))
                {
                    if (!arg.StartsWith("Duel.Get", StringComparison.Ordinal)) continue;
                    expr = arg;   // 内联写法：Duel.Destroy(Duel.GetFieldGroup(...), ...)
                }

                int z = ZoneFlags(expr);
                if (z == None) continue;                       // 组里没有区域 → 不是场上清场

                int byFilter = FilterFlags(expr, whole);
                if (byFilter != None) z = byFilter;

                switch (SideOf(expr))
                {
                    case 0: flags |= z; break;                             // 只拆发动者自己 → 低 2 位
                    case 1: flags |= z << ShiftOpp; break;                  // 只拆对手 → 高 2 位
                    default: flags |= z | (z << ShiftOpp); break;           // 两边都拆
                }
            }
            return flags;
        }

        private static int ZoneFlags(string expr)
        {
            bool onfield = expr.IndexOf("LOCATION_ONFIELD", StringComparison.Ordinal) >= 0
                || expr.IndexOf("LOCATION_ALL", StringComparison.Ordinal) >= 0;
            int f = None;
            if (onfield
                || expr.IndexOf("LOCATION_MZONE", StringComparison.Ordinal) >= 0
                || expr.IndexOf("LOCATION_MONSTERZONE", StringComparison.Ordinal) >= 0)
                f |= Monster;
            if (onfield || expr.IndexOf("LOCATION_SZONE", StringComparison.Ordinal) >= 0)
                f |= SpellTrap;
            return f;
        }

        // 组取函数的 (自己, 对手) 两个区域参数：0 = 只拆自己，1 = 只拆对手，2 = 两边/说不清
        private static int SideOf(string expr)
        {
            string[] a = ArgsOf(expr);
            if (a == null) return 2;

            string self, opp;
            if (expr.StartsWith("Duel.GetFieldGroup", StringComparison.Ordinal))
            {
                if (a.Length < 3) return 2;
                self = a[1];
                opp = a[2];
            }
            else
            {
                if (a.Length < 4) return 2;
                self = a[2];
                opp = a[3];
            }

            bool sZero = self == "0";
            bool oZero = opp == "0";
            if (oZero && !sZero) return 0;
            if (sZero && !oZero) return 1;
            return 2;
        }

        private static string[] ArgsOf(string expr)
        {
            Match m = CallRx.Match(expr);
            if (!m.Success) return null;

            string body = m.Groups[1].Value;
            List<string> list = new List<string>();
            int depth = 0;
            StringBuilder sb = new StringBuilder();
            foreach (char ch in body)
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                if (ch == ',' && depth == 0)
                {
                    list.Add(sb.ToString().Trim());
                    sb.Length = 0;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            list.Add(sb.ToString().Trim());
            return list.ToArray();
        }

        // 组带过滤函数时以过滤函数为准：羽毛扫的 c18144506.filter 只放 TYPE_SPELL+TYPE_TRAP，
        // 虽然区域写的是 LOCATION_ONFIELD，它其实只拆魔陷。
        private static int FilterFlags(string expr, string whole)
        {
            Match m = FirstArgRx.Match(expr);
            if (!m.Success) return None;
            string fn = m.Groups[1].Value;
            if (!fn.StartsWith("c", StringComparison.Ordinal)) return None;

            foreach (Match fm in FuncRx.Matches(whole))
            {
                if (fm.Groups[1].Value != fn) continue;
                string b = fm.Groups[2].Value;
                bool sp = b.IndexOf("TYPE_SPELL", StringComparison.Ordinal) >= 0
                    || b.IndexOf("TYPE_TRAP", StringComparison.Ordinal) >= 0;
                bool mo = b.IndexOf("TYPE_MONSTER", StringComparison.Ordinal) >= 0;
                if (sp && !mo) return SpellTrap;
                if (mo && !sp) return Monster;
                return None;
            }
            return None;
        }
    }
}
