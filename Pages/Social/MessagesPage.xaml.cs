using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class MessagesPage : Page
    {
        private readonly ObservableCollection<ConversationRowViewModel> _conversationRows = new();
        private readonly List<FriendPickerItem> _friendPickerItems = new();
        private List<SavedConversation> _conversations = new();
        private SavedConversation? _activeConversation;
        private MessagesPageArgs? _args;
        private bool _isSelectingConversation;

        public MessagesPage()
        {
            InitializeComponent();
            ConversationList.ItemsSource = _conversationRows;
            Unloaded += MessagesPage_Unloaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _args = e.Parameter as MessagesPageArgs;
            await LoadPageStateAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _ = SaveMessagesAsync();
            base.OnNavigatedFrom(e);
        }

        private async void MessagesPage_Unloaded(object sender, RoutedEventArgs e)
        {
            await SaveMessagesAsync();
        }

        private async Task LoadPageStateAsync()
        {
            StatusText.Text = "";
            _conversations = await LocalMessageStore.Instance.LoadAsync();

            await LoadFriendPickerAsync();

            if (_args?.TargetUserId > 0)
            {
                var conversation = EnsureConversation(
                    _args.TargetUserId,
                    _args.Username,
                    _args.DisplayName);
                SelectConversation(conversation);
            }
            else
            {
                RefreshConversationRows();
                SelectConversation(_conversations.OrderByDescending(c => c.UpdatedUtc).FirstOrDefault());
            }

            UpdateInboxSummary();
        }

        private async Task LoadFriendPickerAsync()
        {
            _friendPickerItems.Clear();

            try
            {
                var friends = await SocialManager.Instance.Api.GetFriendsAsync();
                foreach (var friend in friends.OrderBy(f => string.IsNullOrWhiteSpace(f.DisplayName) ? f.Username : f.DisplayName))
                {
                    _friendPickerItems.Add(new FriendPickerItem
                    {
                        UserId = friend.UserId,
                        Username = friend.Username,
                        DisplayName = friend.DisplayName,
                        Label = string.IsNullOrWhiteSpace(friend.DisplayName)
                            ? $"@{friend.Username}"
                            : $"{friend.DisplayName} (@{friend.Username})"
                    });
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Friends could not be loaded: {ex.Message}";
            }

            foreach (var conversation in _conversations)
            {
                if (_friendPickerItems.Any(f => f.UserId == conversation.TargetUserId))
                    continue;

                _friendPickerItems.Add(new FriendPickerItem
                {
                    UserId = conversation.TargetUserId,
                    Username = conversation.Username,
                    DisplayName = conversation.DisplayName,
                    Label = $"{GetConversationTitle(conversation)} (saved)"
                });
            }

            FriendPickerBox.ItemsSource = null;
            FriendPickerBox.ItemsSource = _friendPickerItems;
        }

        private SavedConversation EnsureConversation(long targetUserId, string username, string displayName)
        {
            var conversation = _conversations.FirstOrDefault(c => c.TargetUserId == targetUserId);
            if (conversation == null)
            {
                conversation = new SavedConversation
                {
                    TargetUserId = targetUserId,
                    Username = string.IsNullOrWhiteSpace(username) ? $"user-{targetUserId}" : username,
                    DisplayName = displayName ?? "",
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                _conversations.Add(conversation);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(username))
                    conversation.Username = username;
                if (!string.IsNullOrWhiteSpace(displayName))
                    conversation.DisplayName = displayName;
            }

            RefreshConversationRows();
            _ = SaveMessagesAsync();
            return conversation;
        }

        private void RefreshConversationRows()
        {
            var activeId = _activeConversation?.TargetUserId;
            _conversationRows.Clear();

            foreach (var conversation in _conversations.OrderByDescending(c => c.UpdatedUtc))
                _conversationRows.Add(CreateRow(conversation));

            if (activeId.HasValue)
            {
                var row = _conversationRows.FirstOrDefault(r => r.TargetUserId == activeId.Value);
                if (row != null)
                {
                    _isSelectingConversation = true;
                    ConversationList.SelectedItem = row;
                    _isSelectingConversation = false;
                }
            }

            UpdateInboxSummary();
        }

        private ConversationRowViewModel CreateRow(SavedConversation conversation)
        {
            var lastMessage = conversation.Messages.LastOrDefault();
            return new ConversationRowViewModel
            {
                TargetUserId = conversation.TargetUserId,
                Title = GetConversationTitle(conversation),
                Subtitle = string.IsNullOrWhiteSpace(conversation.Username) ? "Saved friend" : $"@{conversation.Username}",
                Preview = lastMessage?.Text ?? "No messages yet",
                TimestampText = FormatTimestamp(conversation.UpdatedUtc)
            };
        }

        private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSelectingConversation)
                return;

            if (ConversationList.SelectedItem is ConversationRowViewModel row)
            {
                var conversation = _conversations.FirstOrDefault(c => c.TargetUserId == row.TargetUserId);
                SelectConversation(conversation);
            }
        }

        private void SelectConversation(SavedConversation? conversation)
        {
            _activeConversation = conversation;

            if (conversation == null)
            {
                ThreadTitleText.Text = "Messages";
                ThreadSubtitleText.Text = "Saved conversations stay on this device.";
                InboxNameText.Text = "No active thread";
                InboxPreviewText.Text = "Choose a saved conversation or start a new one.";
                ActiveAvatarText.Text = "Z";
                MessageBox.IsEnabled = false;
                SendButton.IsEnabled = false;
                RenderEmptyState("Choose a conversation to see saved messages.");
                return;
            }

            var title = GetConversationTitle(conversation);
            ThreadTitleText.Text = title;
            ThreadSubtitleText.Text = string.IsNullOrWhiteSpace(conversation.Username) ? "Saved friend" : $"@{conversation.Username}";
            InboxNameText.Text = title;
            InboxPreviewText.Text = conversation.Messages.LastOrDefault()?.Text ?? "No messages yet.";
            ActiveAvatarText.Text = GetInitial(title);
            MessageBox.IsEnabled = true;
            SendButton.IsEnabled = true;

            var row = _conversationRows.FirstOrDefault(r => r.TargetUserId == conversation.TargetUserId);
            if (row != null)
            {
                _isSelectingConversation = true;
                ConversationList.SelectedItem = row;
                _isSelectingConversation = false;
            }

            RenderMessages(conversation);
        }

        private void RenderMessages(SavedConversation conversation)
        {
            MessagesPanel.Children.Clear();

            if (conversation.Messages.Count == 0)
            {
                RenderEmptyState($"Start a conversation with {GetConversationTitle(conversation)}.");
                return;
            }

            foreach (var message in conversation.Messages.OrderBy(m => m.SentUtc))
                MessagesPanel.Children.Add(CreateMessageBubble(message));
        }

        private void RenderEmptyState(string text)
        {
            MessagesPanel.Children.Clear();
            MessagesPanel.Children.Add(new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(18),
                Background = MakeBrush(0x9A, 0x10, 0x14, 0x1B),
                BorderBrush = MakeBrush(0x24, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 540,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = MakeBrush(0xFF, 0xFF, 0xFF, 0xFF),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        private Border CreateMessageBubble(SavedMessage message)
        {
            var stack = new StackPanel
            {
                Spacing = 6
            };

            stack.Children.Add(new TextBlock
            {
                Text = message.Text,
                Foreground = MakeBrush(0xFF, 0xFF, 0xFF, 0xFF),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = message.IsFromMe ? FontWeights.SemiBold : FontWeights.Normal
            });

            stack.Children.Add(new TextBlock
            {
                Text = FormatMessageTime(message.SentUtc),
                Foreground = MakeBrush(0xB8, 0xD8, 0xE4, 0xEC),
                FontSize = 11
            });

            return new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(18),
                Background = message.IsFromMe
                    ? MakeBrush(0xD8, 0x23, 0x57, 0x80)
                    : MakeBrush(0xA8, 0x16, 0x1D, 0x26),
                BorderBrush = MakeBrush(0x2D, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = message.IsFromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 540,
                Child = stack
            };
        }

        private async void StartNewMessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (FriendPickerBox.SelectedItem is not FriendPickerItem friend)
            {
                StatusText.Text = "Choose a friend first.";
                return;
            }

            StatusText.Text = "";
            var conversation = EnsureConversation(friend.UserId, friend.Username, friend.DisplayName);
            SelectConversation(conversation);
            await SaveMessagesAsync();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var text = MessageBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (_activeConversation == null)
            {
                StatusText.Text = "Choose a conversation first.";
                return;
            }

            var message = new SavedMessage
            {
                Text = text,
                IsFromMe = true,
                SentUtc = DateTimeOffset.UtcNow
            };

            _activeConversation.Messages.Add(message);
            _activeConversation.UpdatedUtc = message.SentUtc;
            MessageBox.Text = "";
            StatusText.Text = "Saved.";

            RefreshConversationRows();
            SelectConversation(_activeConversation);
            await SaveMessagesAsync();
        }

        private async Task SaveMessagesAsync()
        {
            try
            {
                await LocalMessageStore.Instance.SaveAsync(_conversations);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Messages could not be saved: {ex.Message}";
            }
        }

        private void UpdateInboxSummary()
        {
            var messageCount = _conversations.Sum(c => c.Messages.Count);
            InboxSummaryText.Text = _conversationRows.Count == 0
                ? "No saved messages yet"
                : $"{_conversationRows.Count} conversation{(_conversationRows.Count == 1 ? "" : "s")} / {messageCount} message{(messageCount == 1 ? "" : "s")}";
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(FriendsPage));
        }

        private static string GetConversationTitle(SavedConversation conversation)
        {
            if (!string.IsNullOrWhiteSpace(conversation.DisplayName))
                return conversation.DisplayName;

            if (!string.IsNullOrWhiteSpace(conversation.Username))
                return conversation.Username;

            return $"User {conversation.TargetUserId}";
        }

        private static string GetInitial(string text)
        {
            var value = string.IsNullOrWhiteSpace(text) ? "Z" : text.Trim();
            return value[..1].ToUpperInvariant();
        }

        private static string FormatTimestamp(DateTimeOffset time)
        {
            var local = time.ToLocalTime();
            if (local.Date == DateTimeOffset.Now.Date)
                return local.ToString("HH:mm");

            return local.ToString("MMM d");
        }

        private static string FormatMessageTime(DateTimeOffset time)
        {
            return time.ToLocalTime().ToString("MMM d, HH:mm");
        }

        private static SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
        {
            return new SolidColorBrush(ColorHelper.FromArgb(a, r, g, b));
        }

        private sealed class ConversationRowViewModel
        {
            public long TargetUserId { get; set; }
            public string Title { get; set; } = "";
            public string Subtitle { get; set; } = "";
            public string Preview { get; set; } = "";
            public string TimestampText { get; set; } = "";
        }

        private sealed class FriendPickerItem
        {
            public long UserId { get; set; }
            public string Username { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Label { get; set; } = "";
        }
    }
}
