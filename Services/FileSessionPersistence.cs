using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Supabase.Gotrue;
using Supabase.Gotrue.Interfaces;

namespace hanabimanga.Services
{
    /// <summary>
    /// 把 Supabase Auth Session 持久化到 %LocalAppData%\hanabimanga\session.json。
    /// 由 Supabase Client 在初始化时 LoadSession 还原;
    /// 登录/Token Refresh 后由 SDK 主动 SaveSession;
    /// SignOut 时 DestroySession 清除。
    /// </summary>
    internal sealed class FileSessionPersistence : IGotrueSessionPersistence<Session>
    {
        private readonly string _path;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public FileSessionPersistence()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "hanabimanga");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "session.json");
        }

        public void SaveSession(Session session)
        {
            _lock.Wait();
            try
            {
                var json = JsonConvert.SerializeObject(session);
                File.WriteAllText(_path, json);
                Debug.WriteLine($"[session] saved (expires_at={session.ExpiresAt():O})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[session] save failed: {ex.Message}");
            }
            finally
            {
                _lock.Release();
            }
        }

        public Session? LoadSession()
        {
            _lock.Wait();
            try
            {
                if (!File.Exists(_path))
                {
                    Debug.WriteLine("[session] no cached session file");
                    return null;
                }

                var json = File.ReadAllText(_path);
                var session = JsonConvert.DeserializeObject<Session>(json);
                Debug.WriteLine(
                    session != null
                        ? $"[session] loaded (expires_at={session.ExpiresAt():O})"
                        : "[session] cached file unreadable, ignoring");
                return session;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[session] load failed: {ex.Message}");
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public void DestroySession()
        {
            _lock.Wait();
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                    Debug.WriteLine("[session] destroyed");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[session] destroy failed: {ex.Message}");
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
