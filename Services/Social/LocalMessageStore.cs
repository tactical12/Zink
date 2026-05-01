using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Zink.Services.Calling;

namespace Zink.Services.Social
{
    public sealed class LocalMessageStore
    {
        private const string FilePrefix = "zink_messages";

        public static LocalMessageStore Instance { get; } = new LocalMessageStore();

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        private LocalMessageStore()
        {
        }

        public async Task<List<SavedConversation>> LoadAsync()
        {
            var filePath = await GetFilePathAsync();
            if (!File.Exists(filePath))
                return new List<SavedConversation>();

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var data = JsonSerializer.Deserialize<MessageStoreData>(json, _jsonOptions);
                return data?.Conversations?
                    .OrderByDescending(c => c.UpdatedUtc)
                    .ToList() ?? new List<SavedConversation>();
            }
            catch
            {
                return new List<SavedConversation>();
            }
        }

        public async Task SaveAsync(IEnumerable<SavedConversation> conversations)
        {
            var filePath = await GetFilePathAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var data = new MessageStoreData
            {
                SavedUtc = DateTimeOffset.UtcNow,
                Conversations = conversations
                    .OrderByDescending(c => c.UpdatedUtc)
                    .ToList()
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }

        private static async Task<string> GetFilePathAsync()
        {
            var userInfo = await TokenStore.Instance.GetUserInfoAsync();
            var ownerId = userInfo?.userId ?? 0;
            var suffix = ownerId > 0 ? ownerId.ToString() : "local";
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, $"{FilePrefix}_{suffix}.json");
        }

        private sealed class MessageStoreData
        {
            public DateTimeOffset SavedUtc { get; set; }
            public List<SavedConversation> Conversations { get; set; } = new();
        }
    }

    public sealed class SavedConversation
    {
        public long TargetUserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<SavedMessage> Messages { get; set; } = new();
    }

    public sealed class SavedMessage
    {
        public string Text { get; set; } = "";
        public bool IsFromMe { get; set; }
        public DateTimeOffset SentUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
