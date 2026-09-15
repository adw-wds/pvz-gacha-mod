using System;
using UnityEngine;

namespace PvzGachaMod
{
    /// <summary>
    /// 全局启动点。Unity 会在启动时读取 <c>抽卡版PVZ_Data\RuntimeInitializeOnLoads.json</c>，
    /// 按其中登记的表调用静态方法；我们把自己的入口登记进去，就得到一个
    /// **与场景无关、不需要改 Assembly-CSharp.dll** 的启动时机。
    ///
    /// 需要安装器做的两件事（见 源码\注册启动点.ps1）：
    ///   1. 往 ScriptingAssemblies.json 的 names 里加 "PvzGachaMod.dll"
    ///   2. 往 RuntimeInitializeOnLoads.json 的 root 里加一条指向本类的记录
    /// </summary>
    public static class ModBootstrap
    {
        private const string LogFile = "PvzGachaMod.log";

        /// <summary>由 Unity 调用（方法必须是无参静态 void）。</summary>
        public static void Init()
        {
            try
            {
                var go = new GameObject("PvzGachaMod");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<ModRuntime>();

                Log("ModBootstrap.Init 成功：已创建常驻 GameObject 并挂上 ModRuntime");
            }
            catch (Exception ex)
            {
                Log("ModBootstrap.Init 失败：" + ex.GetType().Name + "：" + ex.Message);
            }
        }

        internal static void Log(string line)
        {
            // 只能用「读 + 写」自己实现追加：游戏 mscorlib 里 AppendAllText 两个重载都没有
            // （WriteAllText/ReadAllText 的两参版是游戏自己在用的，必存在）
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, LogFile);
                string old = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
                System.IO.File.WriteAllText(path, old + line + "\n");
            }
            catch { }
        }
    }
}
