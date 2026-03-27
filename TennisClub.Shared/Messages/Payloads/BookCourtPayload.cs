namespace TennisClub.Shared.Messages.Payloads;

public class BookCourtPayload
{
    public int CourtNumber { get; set; }
    public Guid Member1Id { get; set; }
    public Guid Member2Id { get; set; }
    public DateTime Date { get; set; }
    public int StartHour { get; set; }
}
