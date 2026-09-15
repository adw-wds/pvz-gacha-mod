using System;
using System.Collections.Generic;
using System.IO;
using PvzShared;
using PvzPanel;

namespace PvzTests
{
    internal static class ChannelTests
    {
        private static string _tempDir;

        public static void Run()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "pvz_channel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            SavePaths.OverrideDir = _tempDir;   // 把通道与存档路径指到临时目录

            try
            {
                CommandApiTests();
                ChannelFileTests();
                SaveHashTests();
            }
            finally
            {
                SavePaths.OverrideDir = null;
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        // ------------------------------------------------------------ CommandApi

        private static void CommandApiTests()
        {
            Console.WriteLine("CommandApi");

            T.Case("设置开关：状态被写入且响应带最新状态", () =>
            {
                var s = new ModSettings();
                var api = new CommandApi(s, new CapabilityRegistry(), null);
                string r = api.HandleCommand("{\"key\":\"gacha.free\",\"value\":true}");

                T.True(s.FreeGacha, "状态应被写入");
                T.Contains(r, "\"ok\":true");
                T.Contains(r, "\"features\":[");
                T.Contains(r, "\"value\":true");
                T.Eq(1, api.CommandsHandled);
            });

            T.Case("未知 key / 非法值 都返回 ok:false + 中文原因", () =>
            {
                var s = new ModSettings();
                var api = new CommandApi(s, new CapabilityRegistry(), null);

                string r1 = api.HandleCommand("{\"key\":\"nope\",\"value\":\"1\"}");
                T.Contains(r1, "\"ok\":false");
                T.Contains(r1, "未知");

                string r2 = api.HandleCommand("{\"key\":\"system.speed\",\"value\":\"快\"}");
                T.Contains(r2, "\"ok\":false");                T.Eq(1f, s.Speed);
            });

            T.Case("坏 JSON 与缺字段都不崩", () =>
            {
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(), null);
                T.Contains(api.HandleCommand("{坏掉的 json"), "\"ok\":false");
                T.Contains(api.HandleCommand("{\"value\":true}"), "\"ok\":false");
                T.Contains(api.HandleCommand(null), "\"ok\":false");
            });

            T.Case("动作：转交处理器并把结果记进状态", () =>
            {
                var log = new List<string>();
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(),
                    (name, value) => { log.Add(name + "|" + value); return "{\"ok\":true,\"detail\":\"已执行\"}"; });

                string r = api.HandleCommand("{\"action\":\"save.giveMoney\",\"value\":\"88888\"}");
                T.Eq("save.giveMoney|88888", log[0]);
                T.Contains(r, "\"ok\":true");
                T.Eq("save.giveMoney", api.LastAction);
                T.Eq("已执行", api.LastActionResult);
                T.True(api.LastActionOk);
            });

            T.Case("动作失败时 ok:false 被记录", () =>
            {
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(),
                    (name, value) => "{\"ok\":false,\"error\":\"找不到存档\"}");

                string r = api.HandleCommand("{\"action\":\"boom\"}");
                T.Contains(r, "\"ok\":false");
                T.True(!api.LastActionOk);
                T.Contains(api.LastActionResult, "找不到存档");
            });

            T.Case("没有动作处理器时友好报错", () =>
            {
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(), null);
                string r = api.HandleCommand("{\"action\":\"x\"}");
                T.Contains(r, "\"ok\":false");
                T.Contains(r, "未就绪");
            });

            // ---- 快照下发：面板每次改动发全量，避免连点丢命令 ----

            T.Case("整份设置快照：一次命令应用多项，且不误伤没提到的项", () =>
            {
                var s = new ModSettings { FreeGacha = false, Speed = 1f, Shelf = "0,1,5,8,10,11" };
                var api = new CommandApi(s, new CapabilityRegistry(), null);

                string r = api.HandleCommand(
                    "{\"gacha.free\":true,\"system.speed\":3,\"money.lockCoin\":true,\"money.coinValue\":1000000000}");

                T.Contains(r, "\"ok\":true");
                T.True(s.FreeGacha, "快照里的开关应被打开");
                T.Eq(3f, s.Speed);
                T.True(s.LockCoin);
                T.Eq(1000000000, s.CoinValue);

                T.Eq("0,1,5,8,10,11", s.Shelf);
                T.Eq(1, api.CommandsHandled);
                T.Contains(api.LastActionResult, "已同步 4 项");
            });

            T.Case("快照里有不认识的 key 也不会翻车（旧面板连新 mod）", () =>
            {
                var s = new ModSettings();
                var api = new CommandApi(s, new CapabilityRegistry(), null);

                string r = api.HandleCommand("{\"gacha.free\":true,\"future.unknownKey\":\"whatever\"}");
                T.Contains(r, "\"ok\":true");
                T.True(s.FreeGacha);
                T.Contains(r, "\"applied\":1");
            });

            T.Case("快照里一个可识别的项都没有 → 明确失败，而不是假装成功", () =>
            {
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(), null);
                string r = api.HandleCommand("{\"nope.one\":1,\"nope.two\":2}");
                T.Contains(r, "\"ok\":false");
                T.Contains(r, "nope.one");     // 报错要点名是哪些 key 不认识，用户才知道去查版本
            });

            // 回归守卫：曾经 Error() 是 static，只拼 JSON 不写状态，
            // 于是状态文件一直说「上一次成功了」，面板永远看不到命令被拒，
            // 开关卡在「同步中」收不了尾 —— 用户只能判断成「功能用不了」。
            T.Case("命令失败必须写进状态：LastActionOk=false 且带上原因", () =>
            {
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(), null);

                api.HandleCommand("{\"gacha.free\":true}");
                T.True(api.LastActionOk, "成功时 LastActionOk 应为 true");

                string r = api.HandleCommand("{\"no.such.key\":1}");
                T.Contains(r, "\"ok\":false");
                T.True(!api.LastActionOk, "失败后 LastActionOk 必须变 false，否则面板是瞎的");
                T.Contains(api.LastActionResult, "no.such.key");
            });

            T.Case("快照部分被忽略 → 已应用的照样生效，但被忽略的项要上报", () =>
            {
                var s = new ModSettings();
                var api = new CommandApi(s, new CapabilityRegistry(), null);

                string r = api.HandleCommand("{\"gacha.free\":true,\"nope.one\":1}");
                T.Contains(r, "\"ok\":true");              // 认得的那部分不能白费
                T.True(s.FreeGacha, "可识别的项要真的写进去");
                T.True(!api.LastActionOk, "有项被忽略就不能静默，否则界面永远停在「同步中」");
                T.Contains(api.LastActionResult, "nope.one");
            });

            T.Case("快照里的值同样会被清洗（速度越界/数值超上限）", () =>
            {
                var s = new ModSettings();
                var api = new CommandApi(s, new CapabilityRegistry(), null);

                api.HandleCommand("{\"system.speed\":999,\"money.coinValue\":99999999999}");
                T.Eq(5f, s.Speed);                          // 夹到 MaxSpeed
                T.Eq(1000000000, s.CoinValue);              // 夹到 10 亿
            });

            T.Case("状态 JSON 含产品信息与能力表", () =>
            {
                var caps = new CapabilityRegistry();
                caps.Register("speed", true);
                caps.Register("bad", false, "目标方法不存在");
                var api = new CommandApi(new ModSettings(), caps, null);

                string st = api.BuildStateJson();
                T.Contains(st, "抽卡版PVZ");
                T.Contains(st, "\"capabilities\":[");
                T.Contains(st, "\"name\":\"speed\"");
                T.Contains(st, "\"ok\":false");
                T.Contains(st, "目标方法不存在");
                // 状态文件是给面板用 System.Text.Json 读的（含嵌套数组），这里只校验开头形状
                T.True(st.StartsWith("{\"ok\":true", StringComparison.Ordinal), st.Substring(0, 20));
            });

            T.Case("CapabilityRegistry 计数正确", () =>
            {
                var caps = new CapabilityRegistry();
                caps.Register("a", true);
                caps.Register("b", false, "x");
                caps.Register("c", true);
                T.Eq(3, caps.Count);
                T.Eq(2, caps.OkCount);
                T.True(caps.IsOk("a"));
                T.True(!caps.IsOk("b"));
                T.Eq("x", caps.ErrorOf("b"));
            });
        }

        // ------------------------------------------------------------ 文件通道

        private static void ChannelFileTests()
        {
            Console.WriteLine("ChannelFiles");

            T.Case("命令文件：写入 → 读到 → 消费后消失", () =>
            {
                T.True(!ChannelFiles.StateExists() || true);   // 占位断言，避免误报
                T.True(ChannelFiles.WriteCommand("{\"key\":\"gacha.free\",\"value\":true}"));

                string cmd = ChannelFiles.ReadCommand();
                T.Contains(cmd, "gacha.free");

                ChannelFiles.ClearCommand();
                T.Eq(null, ChannelFiles.ReadCommand());
            });

            T.Case("状态文件：写入后读回一致", () =>
            {
                ChannelFiles.WriteState("{\"ok\":true,\"n\":1}");
                T.Eq("{\"ok\":true,\"n\":1}", ChannelFiles.ReadState());
                T.True(ChannelFiles.StateExists());
            });

            T.Case("读不存在的命令文件返回 null 而不是抛异常", () =>
            {
                ChannelFiles.ClearCommand();
                T.Eq(null, ChannelFiles.ReadCommand());
                T.Eq(0, ChannelFiles.ReadCommands().Length);
            });

            // ---- 队列式通道：这是「连点丢命令」的根治点，必须锁住 ----

            T.Case("命令队列：多行一次读完，连点的每条都在", () =>
            {
                ChannelFiles.ClearCommand();

                // 模拟面板行为：追加而不是覆盖（面板跑在 .NET 8 上，有 AppendAllText）
                File.AppendAllText(SavePaths.CommandFile, "{\"key\":\"system.speed\",\"value\":2}\n");
                File.AppendAllText(SavePaths.CommandFile, "{\"key\":\"system.speed\",\"value\":3}\n");
                File.AppendAllText(SavePaths.CommandFile, "{\"key\":\"gacha.free\",\"value\":true}\n");

                string[] lines = ChannelFiles.ReadCommands();
                T.Eq(3, lines.Length);
                T.Contains(lines[0], "\"value\":2");
                T.Contains(lines[1], "\"value\":3");
                T.Contains(lines[2], "gacha.free");

                // 逐条处理 → 每一条都真的生效
                var s = new ModSettings();
                var api = new CommandApi(s, new CapabilityRegistry(), null);
                foreach (string line in lines) api.HandleCommand(line);

                T.Eq(3f, s.Speed);
                T.True(s.FreeGacha);
                T.Eq(3, api.CommandsHandled);

                ChannelFiles.ClearCommand();
                T.Eq(0, ChannelFiles.ReadCommands().Length);
            });

            T.Case("队列里的空行/空白不会当成命令", () =>
            {
                ChannelFiles.ClearCommand();
                File.WriteAllText(SavePaths.CommandFile, "\n{\"key\":\"gacha.free\",\"value\":true}\n\n   \n");

                string[] lines = ChannelFiles.ReadCommands();
                T.Eq(1, lines.Length);
                T.Contains(lines[0], "gacha.free");
                ChannelFiles.ClearCommand();
            });

            T.Case("面板把队列当快照发：后来的快照覆盖先前意图", () =>
            {
                var client = new PanelClient();

                var first = new Dictionary<string, string> { { "system.speed", "2" } };
                T.True(client.SendSnapshot(first, out string e1), e1);
                var second = new Dictionary<string, string> { { "system.speed", "5" } };
                T.True(client.SendSnapshot(second, out string e2), e2);

                string[] lines = ChannelFiles.ReadCommands();
                T.Eq(2, lines.Length);

                var s = new ModSettings();
                var api = new CommandApi(s, new CapabilityRegistry(), null);
                foreach (string line in lines) api.HandleCommand(line);

                T.Eq(5f, s.Speed);
                ChannelFiles.ClearCommand();
            });

            T.Case("SendSet / SendAction 也是追加而不是覆盖", () =>
            {
                ChannelFiles.ClearCommand();
                var client = new PanelClient();

                T.True(client.SendSet("gacha.free", "true", out string e1), e1);
                T.True(client.SendAction("save.giveMoney", "88888", out string e2), e2);

                T.Eq(2, ChannelFiles.ReadCommands().Length);
                ChannelFiles.ClearCommand();
            });

            // ---- 脚本：多行源码必须能安全穿过「按行分隔」的命令队列 ----

            T.Case("脚本源码里的换行被转义，不会把命令队列拆坏", () =>
            {
                ChannelFiles.ClearCommand();
                var client = new PanelClient();

                string src = "log 第一行\nset gacha.free on\nwait 500\n";
                T.True(client.SendScript(src, out string e), e);

                string[] lines = ChannelFiles.ReadCommands();
                T.Eq(1, lines.Length);                      // 整份脚本必须只占一行
                T.True(lines[0].IndexOf('\n') < 0, "行里不能有裸换行");

                // 而且解析回来要还是原来的多行源码
                Dictionary<string, string> obj = PvzShared.MiniJson.ParseFlatObject(lines[0]);
                T.Eq(src, obj["script"]);
                ChannelFiles.ClearCommand();
            });

            T.Case("脚本命令走 CommandApi：启动/停止都被记录", () =>
            {
                string started = null, stopped = null;
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(), null)
                {
                    ScriptStarter = s => { started = s; return null; },      // 空 = 启动成功
                    ScriptStopper = () => { stopped = "yes"; },
                    ScriptStatusProvider = () => "{\"running\":true}",
                };

                string r1 = api.HandleCommand("{\"script\":\"log hi\\nset gacha.free on\"}");
                T.Contains(r1, "\"ok\":true");
                T.Contains(started, "set gacha.free on");
                T.Eq("script", api.LastAction);
                T.Contains(api.BuildStateJson(), "\"script\":{\"running\":true}");

                string r2 = api.HandleCommand("{\"scriptStop\":true}");
                T.Contains(r2, "\"ok\":true");
                T.Eq("yes", stopped);
            });

            T.Case("脚本语法错时把原因原样退回给面板", () =>
            {
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(), null)
                {
                    ScriptStarter = s => "语法错误（第 2 行）：不认识的指令「frobnicate」",
                };

                string r = api.HandleCommand("{\"script\":\"frobnicate\"}");
                T.Contains(r, "\"ok\":false");
                T.Contains(r, "frobnicate");
                T.Contains(r, "第 2 行");
                T.True(!api.LastActionOk);
            });

            T.Case("状态里带指令集，面板首页据此渲染速查表", () =>
            {
                var api = new CommandApi(new ModSettings(), new CapabilityRegistry(), null);
                string json = api.BuildStateJson();

                T.Contains(json, "\"instructions\":[");
                T.Contains(json, "set <");
                T.Contains(json, "\"syntax\"");
                T.Contains(json, "\"valueHints\":[");
            });
        }

        // ------------------------------------------------------------ 存档哈希

        private static void SaveHashTests()
        {
            Console.WriteLine("SaveHash");

            T.Case("写哈希后能自校验通过，且文件不含换行", () =>
            {
                string save = SavePaths.SaveFile;
                string hash = SavePaths.HashFile;
                File.WriteAllText(save, "{\"coin\":123}");

                SaveHash.WriteHash(save, hash);
                T.True(SaveHash.Verify(save, hash), "自写自校验应通过");

                string content = File.ReadAllText(hash);
                T.True(content.IndexOf('\n') < 0 && content.IndexOf('\r') < 0, "不应含换行");

                File.WriteAllText(save, "{\"coin\":999}");
                T.True(!SaveHash.Verify(save, hash), "改内容后应校验失败");
            });

            T.Case("真实存档：本算法结果必须与游戏写的 .md5 完全一致", () =>
            {
                string realSave = Path.Combine(SavePaths.Dir, "save.json");
                string realHash = Path.Combine(SavePaths.Dir, "save.json.md5");

                if (!File.Exists(realSave) || !File.Exists(realHash))
                {
                    Console.WriteLine("  SKIP  真实存档向量（还没生成存档）");
                    return;
                }

                T.Eq(File.ReadAllText(realHash).Trim(), SaveHash.ComputeFile(realSave));
            });
        }
    }
}
