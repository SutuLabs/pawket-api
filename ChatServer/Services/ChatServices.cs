
using System.Linq;
using StackExchange.Redis;

namespace ChatServer
{
    public class ChatService
    {
        private const string RedisMessagePrefix = "MSG";
        private readonly ILogger<ChatService> logger;
        private readonly ConnectionMultiplexer redis;

        public ChatService(ILogger<ChatService> logger)
        {
            this.logger = logger;
            this.redis = ConnectionMultiplexer.Connect
                (new ConfigurationOptions { EndPoints = { "10.179.0.196:6379" } });
        }

        public async Task<long> AddMessages(string toPubKey,params string[] messages)
        {
            var db = redis.GetDatabase();
            return await db.ListRightPushAsync(
                new RedisKey(RedisMessagePrefix + toPubKey),
                messages.Select(_ => new RedisValue(_)).ToArray()
                );
        }

        public async Task<string[]> GetMessages(string toPubKey, int count)
        {
            var db = redis.GetDatabase();
            return (await db.ListLeftPopAsync(new RedisKey(RedisMessagePrefix + toPubKey), count))
                .Select(_ => (string)_)
                .ToArray();
        }
    }
}