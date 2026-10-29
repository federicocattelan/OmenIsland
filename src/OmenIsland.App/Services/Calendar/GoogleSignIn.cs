namespace OmenIsland.App.Services.Calendar;

// The OAuth client values live in GoogleSignIn.Local.cs, which is not committed.
// Copy GoogleSignIn.Local.cs.example to GoogleSignIn.Local.cs and fill it in to enable Google Calendar.
internal static partial class GoogleSignIn
{
    public static string ClientId { get; private set; } = "";

    public static string ClientSecret { get; private set; } = "";

    static GoogleSignIn() => LoadLocal();

    static partial void LoadLocal();
}
