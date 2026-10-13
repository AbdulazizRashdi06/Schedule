using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Tests;

public class ClashTests
{
    static Section S(int id, params (int Day, string Start, string End)[] m) => new()
    {
        Id = id,
        Meetings = m.Select(x => new Meeting { Day = x.Day, Start = TimeOnly.Parse(x.Start), End = TimeOnly.Parse(x.End) }).ToList(),
    };

    [Fact]
    public void Overlapping_meetings_on_the_same_day_clash()
    {
        Assert.True(Clashes.Overlap(S(1, (0, "08:00", "09:30")), S(2, (0, "09:00", "10:00"))));
    }

    [Fact]
    public void Back_to_back_classes_do_not_clash()
    {
        Assert.False(Clashes.Overlap(S(1, (0, "08:00", "09:30")), S(2, (0, "09:30", "11:00"))));
    }

    [Fact]
    public void Same_time_on_different_days_does_not_clash()
    {
        Assert.False(Clashes.Overlap(S(1, (0, "08:00", "09:30")), S(2, (1, "08:00", "09:30"))));
    }

    [Fact]
    public void Find_returns_every_clashing_pair()
    {
        var list = new List<Section>
        {
            S(1, (0, "08:00", "09:30"), (2, "08:00", "09:30")),
            S(2, (2, "09:00", "10:00")),
            S(3, (4, "08:00", "09:30")),
            S(4, (0, "08:30", "09:00")),
        };
        var pairs = Clashes.Find(list).Select(p => (p[0], p[1])).ToList();
        Assert.Equal([(1, 2), (1, 4)], pairs);
    }
}
