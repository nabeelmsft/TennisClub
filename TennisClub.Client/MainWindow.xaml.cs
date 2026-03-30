using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TennisClub.Client.Services;
using TennisClub.Shared;
using TennisClub.Shared.Messages;
using TennisClub.Shared.Messages.Payloads;
using TennisClub.Shared.Models;

namespace TennisClub.Client;

public partial class MainWindow : Window
{
    private readonly WebSocketClient _client = new();
    private readonly ObservableCollection<Member> _members = [];
    private readonly ObservableCollection<CourtAvailability> _availability = [];
    private readonly ObservableCollection<Booking> _bookings = [];
    private readonly ObservableCollection<string> _liveFeed = [];

    public MainWindow()
    {
        InitializeComponent();
        MembersListView.ItemsSource = _members;
        AvailabilityGrid.ItemsSource = _availability;
        BookingsGrid.ItemsSource = _bookings;
        Member1ComboBox.ItemsSource = _members;
        Member2ComboBox.ItemsSource = _members;
        LiveFeedListBox.ItemsSource = _liveFeed;

        // Subscribe before connecting so no push is missed
        _client.PushReceived += OnPushReceived;

        // Show reconnection status in the status bar.
        // Both events fire on a thread-pool thread, so we marshal to the UI thread.
        _client.Reconnecting += attempt => Dispatcher.Invoke(() =>
            StatusText.Text = $"Connection lost \u2014 reconnecting\u2026 (attempt {attempt}/5)");
        _client.Reconnected += () =>
            Dispatcher.Invoke(() => StatusText.Text = "Reconnected to server.");

        // Confirmation gate: called automatically by WebSocketClient before every
        // write operation. Non-write (Get*) messages bypass this entirely.
        _client.ConfirmWriteAsync = type =>
        {
            var result = MessageBox.Show(
                $"You are about to perform a write operation:\n\n    \u2022  {type.GetDisplayName()}\n\nDo you want to continue?",
                "Confirm Write Operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return Task.FromResult(result == MessageBoxResult.Yes);
        };

        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _client.ConnectAsync("ws://localhost:5000/");
            StatusText.Text = "Connected to server.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connection failed: {ex.Message}";
        }
    }

    // ── Tab 1: Members ───────────────────────────────────────────────────────

    private async void SignUp_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        var email = EmailTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email))
        {
            MessageBox.Show("Please enter both name and email.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var response = await _client.SendAsync(new WebSocketMessage
            {
                Type = MessageType.SignUp,
                Payload = ToJsonElement(new SignUpPayload { Name = name, Email = email })
            });

            if (response.Success)
            {
                var member = response.GetData<Member>();
                if (member is not null) _members.Add(member);
                NameTextBox.Clear();
                EmailTextBox.Clear();
            }
            else
            {
                MessageBox.Show(response.Error ?? "Sign up failed.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (OperationCanceledException) { /* user chose No — nothing to report */ }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshMembers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshMembersAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshMembersAsync()
    {
        var response = await _client.SendAsync(new WebSocketMessage { Type = MessageType.GetMembers });
        if (response.Success)
        {
            var list = response.GetData<List<Member>>();
            _members.Clear();
            if (list is not null) foreach (var m in list) _members.Add(m);
        }
    }

    // ── Tab 2: Availability ──────────────────────────────────────────────────

    private async void CheckAvailability_Click(object sender, RoutedEventArgs e)
    {
        if (AvailabilityDatePicker.SelectedDate is not { } date)
        {
            MessageBox.Show("Please select a date.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var response = await _client.SendAsync(new WebSocketMessage
            {
                Type = MessageType.GetAvailability,
                Payload = ToJsonElement(new GetAvailabilityPayload { Date = date })
            });

            if (response.Success)
            {
                var list = response.GetData<List<CourtAvailability>>();
                _availability.Clear();
                if (list is not null) foreach (var item in list) _availability.Add(item);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Tab 3: Book a Court ──────────────────────────────────────────────────

    private async void Book_Click(object sender, RoutedEventArgs e)
    {
        if (BookDatePicker.SelectedDate is not { } date)
        { MessageBox.Show("Please select a date.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        if (CourtComboBox.SelectedItem is not ComboBoxItem courtItem)
        { MessageBox.Show("Please select a court.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        if (SlotComboBox.SelectedItem is not ComboBoxItem slotItem)
        { MessageBox.Show("Please select a time slot.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        if (Member1ComboBox.SelectedItem is not Member m1)
        { MessageBox.Show("Please select Member 1.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        if (Member2ComboBox.SelectedItem is not Member m2)
        { MessageBox.Show("Please select Member 2.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        if (m1.Id == m2.Id)
        { MessageBox.Show("Members must be different.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        try
        {
            var response = await _client.SendAsync(new WebSocketMessage
            {
                Type = MessageType.BookCourt,
                Payload = ToJsonElement(new BookCourtPayload
                {
                    CourtNumber = int.Parse((string)courtItem.Tag),
                    StartHour   = int.Parse((string)slotItem.Tag),
                    Member1Id   = m1.Id,
                    Member2Id   = m2.Id,
                    Date        = date
                })
            });

            if (response.Success)
                MessageBox.Show("Court booked successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(response.Error ?? "Booking failed.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (OperationCanceledException) { /* user chose No — nothing to report */ }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Tab 4: Bookings

    private async void RefreshBookings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var response = await _client.SendAsync(new WebSocketMessage { Type = MessageType.GetBookings });
            if (response.Success)
            {
                var list = response.GetData<List<Booking>>();
                _bookings.Clear();
                if (list is not null) foreach (var b in list) _bookings.Add(b);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Tab 5: Live Feed

    /// <summary>
    /// Called on a thread-pool thread by WebSocketClient whenever the server sends
    /// an unsolicited push message (RequestId is empty — no pending TCS to match).
    /// We marshal back to the UI thread via Dispatcher.Invoke before touching collections.
    /// </summary>
    private void OnPushReceived(WebSocketResponse response)
    {
        Dispatcher.Invoke(() =>
        {
            if (response.Type == MessageType.BookingBroadcast)
            {
                var booking = response.GetData<Booking>();
                if (booking is null) return;

                // Add a timestamped line to the live feed
                var line = $"[{DateTime.Now:HH:mm:ss}]  ▶  Court {booking.CourtNumber}  {booking.TimeSlotDisplay}  —  {booking.Member1Name}  +  {booking.Member2Name}";
                _liveFeed.Insert(0, line); // newest at top

                // Keep the Bookings tab up-to-date without a manual refresh
                if (!_bookings.Any(b => b.Id == booking.Id))
                    _bookings.Add(booking);
            }
        });
    }

    private void ClearFeed_Click(object sender, RoutedEventArgs e) => _liveFeed.Clear();

    // ── Tab selection ────────────────────────────────────────────────────────

    private async void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard against events bubbling from nested controls (ListView, DataGrid, etc.)
        if (e.Source is not TabControl) return;

        // Auto-load members when the Book tab is opened so the combo boxes are populated
        if (MainTabControl.SelectedIndex == 2 && _members.Count == 0)
            try { await RefreshMembersAsync(); } catch { /* best effort */ }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _ = _client.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static JsonElement ToJsonElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonConfig.Options);
}