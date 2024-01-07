namespace WalletServer.Helpers;

using Microsoft.Extensions.Caching.Memory;

public static class CacheExtensions
{
    public static async Task<T?> GetByCacheAsync<T>(
        this IMemoryCache memoryCache,
        string key,
        object lockObject,
        Func<Task<T>?> getCacheTask,
        Action<Task<T>?> setCacheTask,
        Func<Task<T>> payloadTask,
        TimeSpan timeSpan)
    {
        if (!memoryCache.TryGetValue(key, out T? cache))
        {
            Task<T> task;
            lock (lockObject)
            {
                task = getCacheTask() ?? payloadTask();
                setCacheTask(task);
            }

            cache = await task;
            lock (lockObject)
            {
                if (getCacheTask() is not null)
                {
                    setCacheTask(null);
                    memoryCache.Set(key, cache, timeSpan);
                }
            }
        }

        return cache;
    }
}