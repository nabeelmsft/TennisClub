using TennisClub.Shared.Models;

namespace TennisClub.Service.Services;

public class BookingService
{
    private static readonly int[] Courts = [1, 2, 3];
    private static readonly int[] TimeSlots = [17, 18, 19]; // 5 PM, 6 PM, 7 PM

    private readonly List<Booking> _bookings = [];
    private readonly object _lock = new();

    public List<CourtAvailability> GetAvailability(DateTime date)
    {
        var day = date.Date;
        lock (_lock)
        {
            var result = new List<CourtAvailability>();
            foreach (var court in Courts)
            {
                foreach (var hour in TimeSlots)
                {
                    var booking = _bookings.FirstOrDefault(b =>
                        b.CourtNumber == court && b.Date.Date == day && b.StartHour == hour);

                    result.Add(new CourtAvailability
                    {
                        CourtNumber = court,
                        StartHour = hour,
                        IsAvailable = booking is null,
                        BookedByMember1 = booking?.Member1Name,
                        BookedByMember2 = booking?.Member2Name
                    });
                }
            }
            return result;
        }
    }

    public (bool Success, string? Error, Booking? Booking) Book(
        int courtNumber, Guid member1Id, string member1Name,
        Guid member2Id, string member2Name, DateTime date, int startHour)
    {
        lock (_lock)
        {
            var conflict = _bookings.Any(b =>
                b.CourtNumber == courtNumber && b.Date.Date == date.Date && b.StartHour == startHour);

            if (conflict) return (false, "That slot is already booked.", null);

            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                CourtNumber = courtNumber,
                Member1Id = member1Id,
                Member1Name = member1Name,
                Member2Id = member2Id,
                Member2Name = member2Name,
                Date = date.Date,
                StartHour = startHour
            };
            _bookings.Add(booking);
            return (true, null, booking);
        }
    }

    public List<Booking> GetAll()
    {
        lock (_lock) return [.. _bookings];
    }
}
