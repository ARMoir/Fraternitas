# Fraternitas

**Prayer · Asceticism · Fraternity**

A private, mobile-friendly group site for Catholic men's fellowship, originally designed around five Advent gatherings and the Joyful Mysteries of the Rosary.

Built with **.NET 10 Blazor Web App (Interactive Server)**, **Radzen Scheduler**, **ASP.NET Core Identity**, **Entity Framework Core** and **SQLite**. The calendar UI takes inspiration from the existing scheduler in ARMoir/Persivia, without importing Persivia-specific services or code.

## Features in this starter

- Invitation-only member accounts (one reusable group link, seven-day expiry and admin revocation)
- Every new and existing member has **Administrator** access by default; the Admin page can explicitly change a user to **Member only**
- Calendar with day/week/month views, add/edit/delete and drag-to-move for admins
- Eastern time meeting schedule, categories and per-event .ics downloads
- Event details with scriptures, reading linkage, description and meeting link
- Five seeded Joyful Mysteries, with the three Lectio Divina prompts
- Group discussion topics and replies
- Per-verse Catholic Scripture pins: discussion topics, replies, or all-day calendar events (calendar pins restricted to admins)
- Private file uploads/downloads (.pdf, .docx, .txt, .png, .jpg/.jpeg, max 10 MB)
- Administrator page for reusable seven-day group invites, revocation and group counts
- Responsive layout usable on desktop and mobile

**Not yet implemented:** outbound email/SMS/push reminders, recurring event rules, calendar subscription feed, RSVP tracking, moderation/deletion interfaces for uploaded files and discussions, multi-group membership, password reset email, and production database migrations. These are follow-up features, not implied by the starter.

## Run locally

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

### First launch in Visual Studio

1. Open Fraternitas in Visual Studio and press **F5** using the **Fraternitas** launch profile.
2. The browser automatically opens **/account/setup** if the database contains no users.
3. Enter your name, email and a **non-empty password**; click **Create administrator**.
4. You are signed in as an Admin. Create member invitations from the **Admin** page.

**No environment variables, preset credentials or database deletion are required on first launch.**
The first-run web wizard works only in **Development on localhost** and is disabled once any account exists. Production setup uses the local terminal command below.

### Recover access to an existing database

Stop the running app (**Shift+F5** in Visual Studio), open PowerShell in the repository root and run:

```powershell
dotnet run --project .\Fraternitas\Fraternitas.csproj -- admin-setup
```

Enter your email and a new password when prompted. For an existing user, the command resets the password, clears lockout and grants Admin access. For a new email, it creates a new Admin account. **It never deletes your events, discussions or uploads.** Start Fraternitas normally again and sign in.

To start without Visual Studio:

```powershell
dotnet run --project .\Fraternitas\Fraternitas.csproj
```

The previous `FRATERNITAS_ADMIN_EMAIL` and `FRATERNITAS_ADMIN_PASSWORD` bootstrap variables are no longer used. Never commit credentials to `launchSettings.json`.

The app creates `Fraternitas/App_Data/fraternitas.db` and `Fraternitas/App_Data/documents/` by default. In local Debug/Release builds these paths are resolved relative to the project directory, regardless of terminal working directory. Set `FRATERNITAS_DATA_DIR` to an absolute path on **persistent private storage** when hosting. Back up the database and document directory together.

To invite members, sign in as administrator, open **Admin**, create **one group invitation link**, then send that same URL to everyone in the group chat. Each person registers an individual account with it, receiving **Administrator privileges by default**. The link remains usable for additional registrations until it expires after seven days or you revoke it from **Admin**. Only the token hash is stored; copy the full URL before leaving the page. Invitations created in older versions and already redeemed stay unusable; generate a fresh shared link.

## Privacy and security

- The GitHub repository is public; **group content is not in the repository**. Store no real member contact information, prayers, or PDFs in code or seed files.
- Calendar, readings, discussions and uploads are protected by authenticated routes. Downloads are protected endpoints, not public static paths.
- All users have **Admin by default**: every invited user can manage the calendar, create/revoke invitation links, pin Scripture onto the calendar, and change member roles. **Shared invitation links therefore grant substantial access; share them carefully.** Members explicitly set to Member only cannot perform administrative actions.
- In **Admin → Member access**, select **Set member only** to downgrade an account, or **Make administrator** to restore it. The app prevents downgrading the last administrator. Role changes take effect when the user signs in again (and sensitive edit actions check the current database role).
- Existing accounts that previously had only the Member role automatically gain Admin on startup **unless an explicit Member-only exception exists**. Exceptions are stored as ASP.NET Identity claims in the current SQLite database and survive restarts; no schema migration, account reset or data deletion is needed.
- Passwords must be non-empty; length/complexity restrictions were intentionally removed for ease of use. Encourage strong unique passwords because group discussions and uploaded documents remain private. Passwords are hashed using ASP.NET Core Identity. Authentication requests, first-run setup and sign-out use antiforgery validation.
- The admin web setup is available only from loopback in Development before the first user exists; existing admin recovery requires local terminal access.
- Invite URLs are secret, reusable by multiple distinct members, expire after seven days, and may be revoked by an administrator. Email addresses are not independently verified in this MVP.
- Uploads are stored outside `wwwroot`, with randomized storage names, extension allowlisting and a 10 MB limit. This is not an antivirus or content-scanning service.
- **Use HTTPS and secure cookie settings in production.** Do not expose a local HTTP development instance on the public internet.
- For real deployment, add monitored backups, email-based recovery, member management, rate limiting, security headers, production migrations and a file scanning policy. Review data retention/privacy expectations with the group.

## Advent 2026 calendar (automatic, one-time seed)

On the first startup after this update, Fraternitas fills the calendar in **both new and existing SQLite databases**, without resetting member accounts or replacing manually scheduled events. A transaction and the `AppSeedHistory` marker prevent duplicate entries on subsequent launches and prevent manually deleted seeded events from reappearing. The marker lives in the existing SQLite database; no EF schema migration is required.

- **Monday gatherings, 8:00–9:00 PM Eastern**: Nov 23 (Annunciation, Luke 1:26–38), Nov 30 (Visitation, Luke 1:39–56), Dec 7 (Nativity, Luke 2:1–20), Dec 14 (Presentation, Luke 2:22–38), Dec 21 (Finding in the Temple, Luke 2:41–52).
- **Wednesday fast until 4:00 PM Eastern + meatless Wednesdays**: Nov 25, Dec 2, 9, 16 and 23.
- **Friday fast until 4:00 PM Eastern**: Nov 27, Dec 4, 11 and 18. Dec 25 (Christmas Day) is not included.
- **Daily personal practices**: all-day reminders Nov 23–Dec 24 for 20 minutes of silent prayer, the daily Ignatian Examen, and the agreed phone/TV asceticism. Their timing is chosen by each member; the calendar has a toggle to show/hide these reminders, hidden by default to keep the month view readable.

Fasting guidance emphasizes **fasting until 4:00 PM Eastern**; the group's alternative is **two small meals and one normal meal**. Each meeting includes three Lectio Divina readings and reflection prompts, an opportunity to discuss the week and one decade of the Rosary. The recurring Zoom URL is left blank pending Ryan's link. The Christmas-season get-together has no confirmed date, so it is mentioned as a discussion item rather than placed on the calendar.

The full meeting transcript initially discusses November 30, but the final agreement is **November 23, 2026, at 8 PM**, continuing for five Mondays through December 21. All local date/time values use `America/New_York`. Day-long observances export as iCalendar `VALUE=DATE` instead of artificial midnight UTC appointments.

### Updating or removing seeded events

The one-time `advent-2026-fasting-until-4pm-v2` update revises the original autogenerated fasting entries in existing databases when their titles and descriptions are unchanged. Manually edited entries are preserved; no database reset is needed.

An administrator can edit or delete entries using the calendar. The importer does not recreate deleted events once the one-time seed has run. It also skips adding a seeded event when an administrator already created an event with the same local start time and category.

**Event calendar entries are not push notifications.** Email/SMS reminders and subscriptions are future features.

## Catholic Scripture readings (HelloAO)

The five Joyful Mystery **Lectio Divina** passages appear on **Readings** and their meeting detail pages. Scripture is fetched at runtime from the [HelloAO Free Use Bible API](https://bible.helloao.org/docs/guide/getting-started.html) using the **Douay–Rheims Bible (1899, English, Catholic)** (`eng_dra`), with verse numbers and source attribution.

**Catholic canon guarantee:** Before accepting any Scripture passage, the server requests `/api/eng_dra/books.json` and verifies the **73 books**, including Tobit, Judith, Wisdom, Sirach, Baruch, 1 Maccabees, and 2 Maccabees. This check uses the actual book inventory, not HelloAO's translation-list summary (which currently misreports 66 books for this edition). The program **never silently switches to a Protestant Bible**.

The live Gospel passages are Luke 1:26–38, Luke 1:39–56, Luke 2:1–20, Luke 2:22–38, and Luke 2:41–52. The API response is cached in server memory for 24 hours. If HelloAO is unavailable or a chapter is incomplete, the page keeps the Scripture reference and a clear error instead of showing an unverified passage. The public domain Douay–Rheims text is for study and group prayer.

To verify all five passages and the Catholic canon from the command line:

```powershell
dotnet run --project .\Fraternitas\Fraternitas.csproj -- verify-scripture
```

This is **not a Catholic daily Mass lectionary**; it retrieves the five specific Gospel passages chosen for the group's Advent Lectio Divina. For Mass readings, a separate approved lectionary source is needed.

## Pin Scripture in the calendar or discussions

From **Readings** (or a Monday meeting's detail page), expand a Douay–Rheims passage and select **📌 Pin** beside any verse. Add an optional reflection (up to 1,200 characters), then:

- **Share in discussions**: any signed-in group member can create a new Scripture discussion topic, containing the canonical Bible reference and verified verse text.
- **Pin on calendar**: administrators can choose a date (Eastern) and create an all-day **Scripture** calendar event that includes the verse and reflection.
- **Pin in this discussion**: from an existing discussion, select **Pin a Bible verse in this discussion**. This opens the readings and adds the chosen verse as a reply to that thread.

Calendar and discussion pages both include shortcuts to the readings for pinning. Pins are saved into existing `CalendarEvent`, `Discussion` and `Reply` records; **no new SQLite migration is needed**. The Bible text is always independently fetched from the verified 73-book Douay–Rheims endpoint before saving. If the API is unavailable, pinning fails without creating an unverified quote. Personal notes are displayed as escaped text, not executable HTML.

Version 1 pins individual verses from the five Joyful Mystery **Luke** passages already shown in Fraternitas. This is not yet a universal Bible browser or Bible-wide text search; that can be added later.

An integration test (`verify-scripture-pins`) uses an isolated SQLite database to verify that a Scripture topic, an existing-thread reply, and an all-day event persist correctly, and that non-admin users cannot pin to the calendar.
## Tech & roadmap

- `Fraternitas/Data`: EF/Identity models and initial seed
- `Fraternitas/Services`: account endpoints, invitation handling, local file storage
- `Fraternitas/Components/Pages`: calendar, readings, events, documents, discussions and administration
- `Fraternitas/wwwroot/app.css`: responsive visual theme
- `.github/workflows/build.yml`: restore/build validation on push and pull request

Next stages: calendar recurrence/reminders, group-level configuration, password recovery, robust migrations and audited production hosting. Keep devotional commitment tracking private and optional.
