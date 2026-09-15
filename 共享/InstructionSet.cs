using System;
using System.Collections.Generic;
using System.Text;

namespace PvzShared
{
    /// <summary>一条指令的说明。面板首页直接拿这份渲染「指令集」表格。</summary>
    public sealed class InstructionDef
    {
        public string Syntax;
        public string Summary;
        public string Example;

        public InstructionDef(string syntax, string summary, string example)
        {
            Syntax = syntax;
            Summary = summary;
            Example = example;
        }
    }

    /// <summary>
    /// 脚本指令集 —— 这是 mod 唯一认得的语言，也是给用户看的文档。
    /// 面板首页会把 All 渲染成一张速查表；解析器 ScriptParser 按同一套规则实现。
    /// 两边共用这一份，改指令只改这里，不会出现「文档和实现对不上」。
    /// </summary>
    public static class InstructionSet
    {
        public static readonly InstructionDef[] All =
        {
            new InstructionDef("set <特性键> <值>", "把某个开关或数值设成指定值", "set gacha.free on"),
            new InstructionDef("toggle <特性键>", "翻转一个开关", "toggle battle.oneHitKill"),
            new InstructionDef("do <动作> [值]", "执行一次性动作（解锁、秒杀、给钱等）", "do save.giveMoney 88888"),
            new InstructionDef("wait <毫秒>", "等一段时间再往下走", "wait 800"),
            new InstructionDef("log <文本>", "往 mod 日志里写一行", "log 进关卡了"),
            new InstructionDef("if scene == <场景>", "按当前场景判断（主菜单=Zhucaidan，关卡内=SampleScene）", "if scene == SampleScene"),
            new InstructionDef("if <特性键> == <值>", "按某个开关的当前值判断", "if battle.oneHitKill == true"),
            new InstructionDef("repeat <次数>", "把一段指令重复若干次", "repeat 3"),
            new InstructionDef("end", "结束一个 if / repeat 块", "end"),
            new InstructionDef("stop", "立刻结束脚本", "stop"),
        };

        /// <summary>值的写法提示（首页一起显示）。</summary>
        public static readonly string[] ValueHints =
        {
            "开关：on / off（也接受 true / false）",
            "数值：直接写，例如 5、0.3、1000000000",
            "特性键：就是「修改器」页每项的名字，鼠标停上去能看到",
        };

        /// <summary>一份可以直接跑的示例脚本（面板「载入示例」用）。</summary>
        public const string SampleScript =
            "# 示例：进关卡后开无敌打法，打完自动关掉\n" +
            "log 脚本开始\n" +
            "do battle.sunFull\n" +
            "set battle.oneHitKill on\n" +
            "set battle.plantGod on\n" +
            "wait 1000\n" +
            "if scene == SampleScene\n" +
            "    set battle.speed 3\n" +
            "    do battle.killAll\n" +
            "end\n" +
            "wait 2000\n" +
            "log 脚本结束\n";

        public static string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < All.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"syntax\":").Append(MiniJson.Quote(All[i].Syntax));
                sb.Append(",\"summary\":").Append(MiniJson.Quote(All[i].Summary));
                sb.Append(",\"example\":").Append(MiniJson.Quote(All[i].Example)).Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string ValueHintsJson()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < ValueHints.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(MiniJson.Quote(ValueHints[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
