namespace TennisClub.Shared.Models;

public class Booking
{
    public Guid Id { get; set; }
    public int CourtNumber { get; set; }
    public Guid Member1Id { get; set; }
    public string Member1Name { get; set; } = string.Empty;
    public Guid Member2Id { get; set; }
    public string Member2Name { get; set; } = string.Empty;
    public DateTime Date { get; set; }

    // The hour the slot starts: 17 = 5 PM, 18 = 6 PM, 19 = 7 PM
    public int StartHour { get; set; }

    public string TimeSlotDisplay => $"{StartHour:D2}:00 - {StartHour + 1:D2}:00";
}
