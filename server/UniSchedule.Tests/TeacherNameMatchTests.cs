using UniSchedule.Api.Auth;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Tests;

/// <summary>How a staff email is matched to a timetable name. The names here are invented, in the shapes the real ones have.</summary>
public class TeacherNameMatchTests
{
    [Theory]
    // Two words.
    [InlineData("karim.hadad", "Karim Hadad")]
    // "First Al Last" in four email spellings.
    [InlineData("yusuf.almamari", "Yusuf Al Mamari")]
    [InlineData("yusuf.al.mamari", "Yusuf Al Mamari")]
    [InlineData("yusuf.al-mamari", "Yusuf Al Mamari")]
    [InlineData("yusuf.mamari", "Yusuf Al Mamari")]
    // "Ouled".
    [InlineData("layla.mansour", "Layla Ouled Mansour")]
    [InlineData("layla.ouledmansour", "Layla Ouled Mansour")]
    // Three and four words without a particle.
    [InlineData("samir.farouk", "Samir Idris Farouk")]
    [InlineData("samir.idrisfarouk", "Samir Idris Farouk")]
    [InlineData("samir.idris.farouk", "Samir Idris Farouk")]
    [InlineData("tariq.mahmoud", "Tariq Adel Hamza Mahmoud")]
    [InlineData("tariq.hamzamahmoud", "Tariq Adel Hamza Mahmoud")]
    [InlineData("tariq.adel.hamza.mahmoud", "Tariq Adel Hamza Mahmoud")]
    // Titles, a hyphen and an umlaut in the name.
    [InlineData("fatma.alrawahi", "Dr. Fatma Al-Rawahi")]
    [InlineData("fatma.rawahi", "Dr. Fatma Al-Rawahi")]
    [InlineData("jens.muller", "Prof. Jens Müller")]
    [InlineData("karim.hadad", "  karim   HADAD ")]
    public void An_email_in_the_university_format_matches_its_name(string local, string name) =>
        Assert.True(TeacherNames.Matches(local, name), $"{local} should match {name}");

    [Theory]
    // Look-alike spellings.
    [InlineData("mohamed.elbehery", "Mohammed El Beheiry")]
    [InlineData("karim.haddad", "Karim Hadad")]
    [InlineData("kareem.hadad", "Karim Hadad")]
    // The middle name only.
    [InlineData("layla.ouled", "Layla Ouled Mansour")]
    [InlineData("samir.idris", "Samir Idris Farouk")]
    [InlineData("tariq.adel", "Tariq Adel Hamza Mahmoud")]
    [InlineData("yusuf.al", "Yusuf Al Mamari")]
    // Initials, reversed, a single word.
    [InlineData("k.hadad", "Karim Hadad")]
    [InlineData("hadad.karim", "Karim Hadad")]
    [InlineData("karim", "Karim Hadad")]
    [InlineData("hadad", "Karim Hadad")]
    [InlineData("karimhadad", "Karim Hadad")]
    // A digit, in the email or in the name.
    [InlineData("karim.hadad1", "Karim Hadad")]
    [InlineData("karim.hadad.2", "Karim Hadad")]
    [InlineData("cs1.cs1", "CS1 CS1")]
    [InlineData("karim.hadad", "Karim Hadad 2")]
    // Underscore forms, and a hyphen between first and last name.
    [InlineData("karim_hadad", "Karim Hadad")]
    [InlineData("yusuf_al_mamari", "Yusuf Al Mamari")]
    [InlineData("yusuf.al_mamari", "Yusuf Al Mamari")]
    [InlineData("karim-hadad", "Karim Hadad")]
    // A surname cut inside a word.
    [InlineData("karim.had.ad", "Karim Hadad")]
    [InlineData("yusuf.alma.mari", "Yusuf Al Mamari")]
    // A different first name with the same surname, and a name with one word.
    [InlineData("jokha.alsaqri", "Hajir Al Saqri")]
    [InlineData("karim.hadad", "Hadad")]
    [InlineData("karim.hadad", "")]
    public void Anything_else_does_not_match(string local, string name) =>
        Assert.False(TeacherNames.Matches(local, name), $"{local} should not match {name}");

    [Fact]
    public void Match_picks_the_one_name_among_shared_first_names_and_surnames()
    {
        string[] names = ["Noor Hassan", "Noor Saleh", "Hajir Al Saqri", "Jokha Al Saqri", "CS1 CS1", "", "noor  hassan"];
        Assert.Equal(["Noor Hassan"], TeacherNames.Match("noor.hassan@gutech.edu.om", names));
        Assert.Equal(["Noor Saleh"], TeacherNames.Match("noor.saleh@gutech.edu.om", names));
        Assert.Equal(["Hajir Al Saqri"], TeacherNames.Match("hajir.alsaqri@gutech.edu.om", names));
        Assert.Equal(["Jokha Al Saqri"], TeacherNames.Match("jokha.saqri@gutech.edu.om", names));
        Assert.Empty(TeacherNames.Match("noor.alsaqri@gutech.edu.om", names));
        // Two different names one address could mean: both come back, so nothing is linked by itself.
        Assert.Equal(2, TeacherNames.Match("omar.saadi@gutech.edu.om", ["Omar Saadi", "Omar Al Saadi"]).Count);
    }

    [Fact]
    public void Match_never_returns_a_placeholder()
    {
        // "Lab Lab" would match lab.lab by the words alone.
        Assert.True(TeacherNames.Matches("lab.lab", "Lab Lab"));
        Assert.Empty(TeacherNames.Match("lab.lab@gutech.edu.om", ["Lab Lab"]));
    }

    [Theory]
    [InlineData("CS1 CS1")]
    [InlineData("CS3 CS3")]
    [InlineData("CS4 CS4")]
    [InlineData("engineering1 engineering1")]
    [InlineData("IBSM1 IBSM1")]
    [InlineData("LS1 LS1")]
    [InlineData("LS2 LS2")]
    [InlineData("LOG4 LOG4")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Staff")]
    [InlineData("Staff staff")]
    [InlineData("to be announced")]
    [InlineData("Karim Hadad 2")]
    public void Placeholders_are_not_people(string? name) => Assert.True(TeacherNames.IsPlaceholder(name));

    [Theory]
    [InlineData("Karim Hadad")]
    [InlineData("Dr. Fatma Al-Rawahi")]
    [InlineData("Prof. Jens Müller")]
    [InlineData("Layla Ouled Mansour")]
    [InlineData("karim Hadad")]
    public void Real_names_are_not_placeholders(string name) => Assert.False(TeacherNames.IsPlaceholder(name));

    [Theory]
    [InlineData("Dr. Fatma Al-Rawahi", "dr fatma al rawahi")]
    [InlineData("  dr FATMA   al rawahi ", "dr fatma al rawahi")]
    [InlineData("DR.FATMA.AL_RAWAHI", "dr fatma al rawahi")]
    [InlineData("Jens Müller", "jens muller")]
    [InlineData("JENS  MULLER", "jens muller")]
    [InlineData("Zoé Çelik-Ñuñez", "zoe celik nunez")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NameKey_ignores_case_spacing_punctuation_and_accents(string? name, string key) => Assert.Equal(key, TeacherNames.NameKey(name));

    [Fact]
    public void Tokens_drop_leading_titles_but_never_a_two_word_name()
    {
        Assert.Equal(["fatma", "al", "rawahi"], TeacherNames.Tokens("Prof. Dr. Fatma Al-Rawahi"));
        Assert.Equal(["eng", "saleh"], TeacherNames.Tokens("Eng. Saleh"));
    }

    [Theory]
    [InlineData("karim.hadad@gutech.edu.om", "karim.hadad@gutech.edu.om")]
    [InlineData("  Karim.Hadad@GUtech.edu.om ", "karim.hadad@gutech.edu.om")]
    [InlineData("karim_hadad-2@gutech.edu.om", "karim_hadad-2@gutech.edu.om")]
    // Exactly the staff domain: not the student one, a sub-domain or a look-alike host.
    [InlineData("24-0161@student.gutech.edu.om", null)]
    [InlineData("karim.hadad@mail.gutech.edu.om", null)]
    [InlineData("karim.hadad@gutech.edu.om.evil.com", null)]
    [InlineData("karim.hadad@evilgutech.edu.om", null)]
    [InlineData("karim.hadad@gutech.edu", null)]
    // Nothing that could break the comma-joined device cookie or isn't an address.
    [InlineData("karim,hadad@gutech.edu.om", null)]
    [InlineData("karim hadad@gutech.edu.om", null)]
    [InlineData("karim..hadad@gutech.edu.om", null)]
    [InlineData(".karim@gutech.edu.om", null)]
    [InlineData("karim.@gutech.edu.om", null)]
    [InlineData("karim@hadad@gutech.edu.om", null)]
    [InlineData("karim.hadad@gutech.edu.om\nx", null)]
    [InlineData("@gutech.edu.om", null)]
    [InlineData("karim.hadad", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_accepts_the_staff_domain_only(string? raw, string? email) => Assert.Equal(email, TeacherEmails.Normalize(raw));

    [Fact]
    public void Normalize_refuses_an_over_long_address()
    {
        const string domain = "@gutech.edu.om";
        Assert.NotNull(TeacherEmails.Normalize(new string('a', 100 - domain.Length) + domain));
        Assert.Null(TeacherEmails.Normalize(new string('a', 101 - domain.Length) + domain));
    }

    [Theory]
    [InlineData("karim.hadad@gutech.edu.om", "Karim Hadad")]
    [InlineData("karim.hadad.2@gutech.edu.om", "Karim Hadad 2")]
    [InlineData("karim.hadad2@gutech.edu.om", "Karim Hadad2")]
    [InlineData("karim_al-hadad@gutech.edu.om", "Karim Al Hadad")]
    [InlineData("registrar@gutech.edu.om", "Registrar")]
    public void DisplayName_is_the_name_in_the_address_with_digits_kept(string email, string name) => Assert.Equal(name, TeacherEmails.DisplayName(email));

    static TeacherName Link(Teacher t, string name, string state) =>
        new() { Id = name.Length, TeacherId = t.Id, Name = name, NameKey = TeacherNames.NameKey(name), State = state };

    [Fact]
    public void Shown_is_the_held_timetable_name_else_the_name_in_the_email()
    {
        var staff = new Teacher { Id = 1, Email = "karim.hadad@gutech.edu.om" };
        HashSet<string> nobody = [];
        Assert.Equal("Karim Hadad", TeacherNames.Shown(staff, [], nobody));
        // The name as written on the timetable, even though it is "taken" (by this very account).
        HashSet<string> taken = [TeacherNames.NameKey("Dr. Karim Hadad")];
        Assert.Equal("Dr. Karim Hadad", TeacherNames.Shown(staff, [Link(staff, "Dr. Karim Hadad", TeacherName.Auto)], taken));
        // A pending or rejected link gives no name, and someone else's link is not theirs.
        Assert.Equal("Karim Hadad", TeacherNames.Shown(staff, [Link(staff, "Noor Saleh", TeacherName.Pending), Link(staff, "Noor Hassan", TeacherName.Rejected)], nobody));
        Assert.Equal("Karim Hadad", TeacherNames.Shown(staff, [Link(new Teacher { Id = 2 }, "Noor Saleh", TeacherName.Approved)], nobody));
    }

    [Fact]
    public void An_unlinked_account_is_never_shown_under_a_real_teachers_name()
    {
        HashSet<string> taken = [TeacherNames.NameKey("Karim Hadad")];
        // Its email name equals an instructor's or a held name: shown by its local part as typed.
        Assert.Equal("karim_hadad", TeacherNames.Shown(new Teacher { Id = 1, Email = "karim_hadad@gutech.edu.om" }, [], taken));
        Assert.Equal("karim-hadad", TeacherNames.Shown(new Teacher { Id = 1, Email = "karim-hadad@gutech.edu.om" }, [], taken));
        var pending = new Teacher { Id = 3, Email = "karim.hadad@gutech.edu.om" };
        Assert.Equal("karim.hadad", TeacherNames.Shown(pending, [Link(pending, "Karim Hadad", TeacherName.Pending)], taken));
        // Someone else's name is free to show.
        Assert.Equal("Noor Saleh", TeacherNames.Shown(new Teacher { Id = 4, Email = "noor.saleh@gutech.edu.om" }, [], taken));

        // A student's teaching profile: the name the owner typed, unless it is a teacher's.
        Assert.Equal("Lab Helper", TeacherNames.Shown(new Teacher { Id = 5, StudentId = 9, DisplayName = "Lab Helper" }, [], taken));
        Assert.Equal("Teaching assistant", TeacherNames.Shown(new Teacher { Id = 5, StudentId = 9, DisplayName = "karim  HADAD" }, [], taken));
        Assert.Equal("Teaching assistant", TeacherNames.Shown(new Teacher { Id = 5, StudentId = 9 }, [], taken));
    }
}
