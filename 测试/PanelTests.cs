using System;
using System.Collections.Generic;
using System.IO;
using PvzPanel;
using PvzShared;

namespace PvzTests
{
    internal static class PanelTests
    {
        public static void Run()
        {
            ParseTests();
            SaveEditorTests();
            LiveRoundTripTest();
        }

        // ------------------------------------------------------------ 状态解析

        private static void ParseTests()
        {
            Console.WriteLine("PanelClient");

            const string sample =
                "{\"ok\":true,\"product\":\"抽卡版PVZ\",\"version\":\"0.60.0\",\"modVersion\":\"1.0.0\"," +
                "\"commandsHandled\":7,\"lastAction\":\"save.giveMoney\",\"lastActionOk\":true,\"lastResult\":\"金币已设为 88888\"," +
                "\"capabilities\":[{\"name\":\"存档通道\",\"ok\":true,\"error\":\"\"},{\"name\":\"卡牌私有字段\",\"ok\":false,\"error\":\"取不到 pro\"}]," +
                "\"features\":[{\"key\":\"gacha.free\",\"label\":\"抽卡免费\",\"group\":\"抽卡\",\"kind\":\"bool\",\"value\":false}," +
                "{\"key\":\"system.speed\",\"label\":\"游戏速度\",\"group\":\"战斗\",\"kind\":\"float\",\"value\":1.5}]}";

            T.Case("解析状态文件：特性、动作结果、失败能力都能读出", () =>
            {
                ModState st = PanelClient.ParseState(sample);
                T.True(st.Ok);
                T.Eq("抽卡版PVZ", st.Product);
                T.Eq("0.60.0", st.Version);
                T.Eq(7, st.CommandsHandled);
                T.Eq("金币已设为 88888", st.LastResult);
                T.Eq(2, st.Features.Count);
                T.Eq("false", st.Find("gacha.free").Raw);
                T.Eq(1.5, st.Find("system.speed").NumValue);
                T.Eq(1, st.FailedCapabilities.Count);
                T.Contains(st.FailedCapabilities[0], "卡牌私有字段");
            });

            T.Case("解析坏 JSON 不抛异常，转为 Ok=false", () =>
            {
                ModState st = PanelClient.ParseState("这不是 json");
                T.True(!st.Ok);
                T.True(st.Error.Length > 0, st.Error);
            });

            T.Case("值编码：bool 裸字面量 / 数字裸数字 / 其余带引号", () =>
            {
                T.Eq("true", PanelClient.EncodeValue("true"));
                T.Eq("3", PanelClient.EncodeValue("3"));
                T.Eq("1.5", PanelClient.EncodeValue("1.5"));
                T.Eq("\"0,1,5\"", PanelClient.EncodeValue("0,1,5"));
            });
        }

        // ------------------------------------------------------------ 存档编辑

        private static void SaveEditorTests()
        {
            Console.WriteLine("SaveEditorLogic");

            const string sample =
                "{\"scores\":[0,1,5],\"coin\":100,\"music\":0.3,\"shangdianYishou\":[],\"chuizhitongbu\":true}";

            T.Case("摊平字段：标量与数组都能读", () =>
            {
                Dictionary<string, string> f = SaveEditorLogic.FlattenFields(sample);
                T.Eq("100", f["coin"]);
                T.Eq("0.3", f["music"]);
                T.Eq("true", f["chuizhitongbu"]);
                T.Eq("[0,1,5]", f["scores"]);
                T.Eq("[]", f["shangdianYishou"]);
            });

            T.Case("改字段：只改被编辑的，其余原样保留", () =>
            {
                var edits = new Dictionary<string, string> { { "coin", "99999" } };
                string result = SaveEditorLogic.ApplyEdits(sample, edits);
                T.Contains(result, "\"coin\":99999");
                T.Contains(result, "\"music\":0.3");
                T.Contains(result, "\"scores\":[0,1,5]");
                T.Contains(result, "\"chuizhitongbu\":true");
            });

            T.Case("数组字段也能改写", () =>
            {
                var edits = new Dictionary<string, string> { { "scores", "[0,1,5,146,150]" } };
                T.Contains(SaveEditorLogic.ApplyEdits(sample, edits), "\"scores\":[0,1,5,146,150]");
            });

            T.Case("受保护字段 chuizhitongbu 永远不被改写", () =>
            {
                var edits = new Dictionary<string, string> { { "chuizhitongbu", "false" } };
                T.Contains(SaveEditorLogic.ApplyEdits(sample, edits), "\"chuizhitongbu\":true");
            });

            T.Case("非法 JSON 原样抛出，不静默破坏", () =>
            {
                bool threw = false;
                try { SaveEditorLogic.ApplyEdits("{坏", new Dictionary<string, string>()); }
                catch { threw = true; }
                T.True(threw);
            });

            T.Case("临时目录：保存自动备份 + 哈希自校验；回滚还原也对", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "pvz_panel_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string save = Path.Combine(dir, "save.json");
                string hash = Path.Combine(dir, "save.json.md5");
                string backupDir = Path.Combine(dir, "backup");

                try
                {
                    File.WriteAllText(save, sample);
                    SaveHash.WriteHash(save, hash);

                    bool ok = SaveEditorLogic.SaveWithBackupTo(save, hash, backupDir,
                        SaveEditorLogic.ApplyEdits(sample, new Dictionary<string, string> { { "coin", "777" } }),
                        out string backupPath, out string error);

                    T.True(ok, error);
                    T.True(File.Exists(backupPath), "应留下备份");
                    T.Contains(File.ReadAllText(save), "\"coin\":777");
                    T.True(SaveHash.Verify(save, hash), "写后哈希应自校验通过");

                    T.True(SaveEditorLogic.RestoreTo(save, hash, backupPath, out string rerr), rerr);
                    T.Contains(File.ReadAllText(save), "\"coin\":100");
                    T.True(SaveHash.Verify(save, hash), "还原后哈希应自校验通过");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            T.Case("外部存档（手机版）：md5 与备份都跟着存档走，且推导结果与电脑版约定一致", () =>
            {
                // ★ 这条是回归护栏：新加的路径推导必须和原来的电脑版约定逐字一致，
                //   否则「切回电脑版存档」会悄悄把存档/校验/备份写到别的地方去。
                T.Eq(SavePaths.HashFile, SaveEditorLogic.HashPathOf(SavePaths.SaveFile));
                T.Eq(Path.Combine(SavePaths.Root, "backup"), SaveEditorLogic.BackupDirOf(SavePaths.SaveFile));

                string dir = Path.Combine(Path.GetTempPath(), "pvz_ext_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string save = Path.Combine(dir, "save.json");

                try
                {
                    T.Eq(save + ".md5", SaveEditorLogic.HashPathOf(save));
                    T.Eq(Path.Combine(dir, "backup"), SaveEditorLogic.BackupDirOf(save));

                    File.WriteAllText(save, sample);

                    string loaded = SaveEditorLogic.LoadTextFrom(save, out string lerr);
                    T.True(lerr == null, lerr);
                    T.Eq(sample, loaded);

                    bool ok = SaveEditorLogic.SaveWithBackupToPath(save,
                        SaveEditorLogic.ApplyEdits(sample, new Dictionary<string, string> { { "coin", "888" } }),
                        out string bp, out string err);

                    T.True(ok, err);
                    T.Contains(File.ReadAllText(save), "\"coin\":888");
                    T.True(SaveHash.Verify(save, SaveEditorLogic.HashPathOf(save)),
                        "手机存档的 .md5 应重算并通过");
                    T.True(File.Exists(bp), "应留下备份");
                    T.True(bp.StartsWith(dir, StringComparison.OrdinalIgnoreCase),
                        "备份不该跑到电脑版目录去：" + bp);

                    T.Eq(1, SaveEditorLogic.RestoreBackupsFrom(SaveEditorLogic.BackupDirOf(save)).Length);

                    T.True(SaveEditorLogic.RestoreToPath(save, bp, out string rerr), rerr);
                    T.Contains(File.ReadAllText(save), "\"coin\":100");
                    T.True(SaveHash.Verify(save, SaveEditorLogic.HashPathOf(save)),
                        "还原后哈希应自校验通过");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            T.Case("拷错文件要挡住：wujin.json / 坏 JSON / 不存在的文件都不该被当成存档", () =>
            {
                // 手机存档是用户自己从手机里拷出来的，拿错文件很常见；
                // 拿错还往里写，游戏就会读存档失败。这里断言三道防线。
                string dir = Path.Combine(Path.GetTempPath(), "pvz_wrong_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);

                try
                {
                    string notSave = Path.Combine(dir, "wujin.json");
                    File.WriteAllText(notSave, "{\"wujin\":0,\"anotherGame\":true}");
                    T.True(!SaveEditorLogic.LooksLikeSaveFile(notSave, out string why), "不该认成存档");
                    T.True(!string.IsNullOrEmpty(why), "要说明原因，用户才知道该换哪个文件");

                    string bad = Path.Combine(dir, "bad.json");
                    File.WriteAllText(bad, "{坏");
                    T.True(!SaveEditorLogic.LooksLikeSaveFile(bad, out string _), "非法 JSON 不该认成存档");

                    string good = Path.Combine(dir, "save.json");
                    File.WriteAllText(good, sample);
                    T.True(SaveEditorLogic.LooksLikeSaveFile(good, out string whyGood), whyGood);

                    T.True(!SaveEditorLogic.LooksLikeSaveFile(Path.Combine(dir, "nope.json"), out string _),
                        "不存在的文件不该认成存档");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            T.Case("读外部存档：文件不存在时，错误信息要点名是哪个路径", () =>
            {
                string missing = Path.Combine(Path.GetTempPath(),
                    "pvz_missing_" + Guid.NewGuid().ToString("N") + ".json");

                string got = SaveEditorLogic.LoadTextFrom(missing, out string err);
                T.True(got == null, "不该返回内容");
                T.Contains(err, missing);

                T.True(SaveEditorLogic.LoadTextFrom("", out string err2) == null, "空路径也该拒绝");
                T.True(!string.IsNullOrEmpty(err2));
            });

            T.Case("默认存档：数组字段全部建全，避免游戏读成 null", () =>
            {
                string json = SaveEditorLogic.SynthesizeDefaultSaveJson();
                T.Contains(json, "\"scores\":[0,1,5]");
                T.Contains(json, "\"shangdian\":[]");
                T.Contains(json, "\"shangdianYishou\":[]");
                T.Contains(json, "\"playerNames\":");
                T.Contains(json, "\"chuizhitongbu\":true");
                T.Contains(json, "\"heng\":1280");
                T.Contains(json, "\"shu\":720");
            });
        }

        // ------------------------------------------------------------ 与运行中的 mod 真实往返

        private static void LiveRoundTripTest()
        {
            Console.WriteLine("实机往返（需要游戏正在运行）");

            T.Case("面板客户端 → 运行中的 mod：设置一个开关并读回", () =>
            {
                var client = new PanelClient();
                client.ReadState();

                if (!client.GameProcessRunning)
                {
                    Console.WriteLine("  SKIP  游戏没在跑（或没装 mod）");
                    return;
                }

                // 本作未开启 Run In Background：游戏失焦就停主循环，状态文件会停住。
                // 默认不抢用户焦点；想跑完整往返就临时切一下。
                if (!client.StateFresh)
                {
                    if (Environment.GetEnvironmentVariable("PVZMOD_TEST_FOCUS") != "1")
                    {
                        Console.WriteLine("  SKIP  游戏在后台暂停（失焦）。" +
                                          "设 PVZMOD_TEST_FOCUS=1 可自动切前台后重跑");
                        return;
                    }

                    GameInfo.FocusGame();
                    for (int i = 0; i < 30 && !client.StateFresh; i++)
                    {
                        System.Threading.Thread.Sleep(200);
                        client.ReadState();
                    }
                    T.True(client.StateFresh, "切前台后游戏应该恢复写状态文件");
                }

                int before = client.Last.CommandsHandled;

                // 用一个无害且可回读的值：游戏速度
                T.True(client.SendSet("system.speed", "1", out string err), err);

                // mod 每 6 帧轮询一次命令，这里等它处理
                for (int i = 0; i < 20 && client.Last.CommandsHandled == before; i++)
                {
                    System.Threading.Thread.Sleep(150);
                    client.ReadState();
                }

                T.True(client.Last.CommandsHandled > before, "mod 应该已经处理过命令");
                ModFeature speed = client.Last.Find("system.speed");
                T.True(speed != null, "状态里应该有 system.speed");
                T.Eq(1.0, speed.NumValue);
            });
        }
    }
}
