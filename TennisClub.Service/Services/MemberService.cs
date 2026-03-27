using TennisClub.Shared.Models;

namespace TennisClub.Service.Services;

public class MemberService
{
    private readonly List<Member> _members = [];
    private readonly object _lock = new();

    public Member Add(string name, string email)
    {
        var member = new Member
        {
            Id = Guid.NewGuid(),
            Name = name,
            Email = email,
            JoinedAt = DateTime.UtcNow
        };
        lock (_lock) _members.Add(member);
        return member;
    }

    public List<Member> GetAll()
    {
        lock (_lock) return [.. _members];
    }

    public Member? GetById(Guid id)
    {
        lock (_lock) return _members.FirstOrDefault(m => m.Id == id);
    }
}
