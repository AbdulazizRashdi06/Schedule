using System.Text.RegularExpressions;

namespace UniSchedule.Api.Schedule;

/// <summary>
/// What kind of class a session is. The timetable doesn't say, so it is read from the room the way the app does:
/// "Lab L2" is a lab, "Tut 3" a tutorial, anything else a lecture.
/// </summary>
public static partial class SessionKinds
{
    public const string Lecture = "lecture", Lab = "lab", Tutorial = "tutorial";

    [GeneratedRegex(@"\blab", RegexOptions.IgnoreCase)]
    private static partial Regex LabRoom();
    [GeneratedRegex(@"\btut", RegexOptions.IgnoreCase)]
    private static partial Regex TutorialRoom();

    /// <summary>The kind of the class held in this room: lecture, lab or tutorial.</summary>
    public static string Of(string? room) => room is null ? Lecture : LabRoom().IsMatch(room) ? Lab : TutorialRoom().IsMatch(room) ? Tutorial : Lecture;

    public static bool Valid(string? kind) => kind is Lecture or Lab or Tutorial;

    /// <summary>The kind as people read it: "Lab".</summary>
    public static string Label(string kind) => kind switch { Lab => "Lab", Tutorial => "Tutorial", _ => "Lecture" };
}
