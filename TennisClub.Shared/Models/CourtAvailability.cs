namespace TennisClub.Shared.Models;

public class CourtAvailability
{
    public int CourtNumber { get; set; }
    public int StartHour { get; set; }
    public bool IsAvailable { get; set; }
    public string? BookedByMember1 { get; set; }
    public string? BookedByMember2 { get; set; }

    public string TimeSlotDisplay => $"{StartHour:D2}:00 - {StartHour + 1:D2}:00";
    public string StatusDisplay => IsAvailable
        ? "Available"
        : $"Booked  {BookedByMember1} + {BookedByMember2}";
}
