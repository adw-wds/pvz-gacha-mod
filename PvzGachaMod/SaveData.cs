using System;
using System.IO;
using PvzShared;
using UnityEngine;

namespace PvzGachaMod
{
    /// <summary>
    /// 存档读写（游戏内一侧）。编译期引用了 Assembly-CSharp，所以 GameStart.GameData 可以强类型访问。
    /// 每次写盘后用 SaveHash 重算 save.json.md5 —— 不用游戏自己的 GameStart.xieHash()，
    /// 因为它在 First.ZUOBIZHE 为真时会拒绝写哈希（GameStart.cs:166-175）。
    /// </summary>
    internal static class SaveData
    {
        internal static GameStart.GameData TryLoad()
        {
            try
            {
                if (!File.Exists(SavePaths.SaveFile)) return null;
                return JsonUtility.FromJson<GameStart.GameData>(File.ReadAllText(SavePaths.SaveFile));
            }
            catch (Exception ex)
            {
                ModRuntime.LogOnce("读取存档", ex);
                return null;
            }
        }

        internal static bool Save(GameStart.GameData data, out string error)
        {
            error = null;
            if (data == null) { error = "存档数据为空"; return false; }

            try
            {
                SavePaths.EnsureDirExists();
                File.WriteAllText(SavePaths.SaveFile, JsonUtility.ToJson(data));
                SaveHash.WriteHash(SavePaths.SaveFile, SavePaths.HashFile);
                return true;
            }
            catch (Exception ex)
            {
                error = "写存档失败：" + ex.Message;
                ModRuntime.LogOnce("写存档", ex);
                return false;
            }
        }

        internal static bool Mutate(Action<GameStart.GameData> change, out string error)
        {
            GameStart.GameData data = TryLoad();
            if (data == null) { error = "找不到存档（请先启动一次游戏并进入关卡）"; return false; }

            try { change(data); }
            catch (Exception ex) { error = "修改存档失败：" + ex.Message; return false; }

            return Save(data, out error);
        }
    }
}
