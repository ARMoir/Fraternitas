# Fraternitas

**Prayer · Penance · Fraternity**

[![Build and tests](https://github.com/ARMoir/Fraternitas/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/ARMoir/Fraternitas/actions/workflows/build.yml)

A private, mobile-friendly group site for Catholic men's fellowship, originally designed around five Advent gatherings and the Joyful Mysteries of the Rosary.

Designed to support prayer and fellowship without becoming another digital distraction. Built with **.NET 10**, **server-rendered Blazor/Razor components**, **ASP.NET Core Identity**, **Entity Framework Core** and **SQLite**. Ordinary HTTP forms and links work without a persistent Blazor Server circuit. The current installation runs on a Raspberry Pi 2 Model B behind a Cloudflare Tunnel, with Resend for outbound email.

## Features

- Reusable group invitations and public account requests requiring administrator approval; private registration links expire in seven days
- All new registrations start as Member only; administrators can promote users after registration
- Automatic password reset emails and administrator-generated recovery links, with manual fallback
- Weekly home page calendar summary and day/week/month calendar with in-page event details and creation for all members and edit/delete modals for event owners and administrators
- Persistent per-member daily prayer and examination of conscience visibility; five appointments per month cell, with a link to view more
- Eastern time meeting schedule, categories and per-event .ics downloads
- Event details with scriptures, reading linkage, description and meeting link
- Five seeded Joyful Mysteries, with the three Lectio Divina prompts
- Complete 73-book Douay–Rheims Bible browser in discussions, with book/chapter/optional verse selection
- Group discussion topics and replies
- Per-verse Catholic Scripture pins: discussion topics, replies, or all-day calendar events (all members can pin on the calendar)
- Private file uploads/downloads (.pdf, .docx, .txt, .png, .jpg/.jpeg, max 10 MB)
- Administrator page for reusable seven-day group invites, revocation and group counts
- Responsive layout usable on desktop and mobile

**Not yet implemented:** scheduled event reminders (email/SMS/push), recurring event rules, calendar subscription feeds, RSVP tracking, multi-group membership, and a full EF migrations framework. Transactional registration and recovery emails are implemented.

## Run locally

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

### First launch in Visual Studio

1. Open Fraternitas in Visual Studio and press **F5** using the **Fraternitas** launch profile.
2. The browser automatically opens **/account/setup** if the database contains no users.
3. Enter your name, email and a **non-empty password**; click **Create administrator**.
4. You are signed in as an Admin. Create member invitations from the **Admin** page.

**No environment variables, preset credentials or database deletion are required on first launch.**
The first-run web wizard works only in **Development on localhost** and is disabled once any account exists. Production setup uses the local terminal command below.

## Hosting on a Raspberry Pi

The application is intended to run as a systemd service with persistent SQLite data outside the deployed application directory. Keep the existing database, documents, and private configuration when updating. GitHub Actions builds and tests the repository; it does not deploy to the Pi automatically.

Use your existing Cloudflare Tunnel to publish the app's local HTTP listener over HTTPS. Multiple hostnames can point to the same listener. Set `FRATERNITAS_PUBLIC_URL` to the preferred public HTTPS origin so that newly generated registration and recovery links use it. Existing links remain valid while their original hostname is still routed to the same application. Authentication cookies are hostname-specific.

Example one-time systemd configuration (adjust the service name if necessary):

```bash
sudo install -d -m 700 /etc/fraternitas
sudo nano /etc/fraternitas/resend.env
sudo chmod 600 /etc/fraternitas/resend.env
sudo systemctl edit fraternitas
```

In the systemd override, insert these lines above the comment that says edits below it are discarded:

```ini
[Service]
EnvironmentFile=/etc/fraternitas/resend.env
Environment="FRATERNITAS_PUBLIC_URL=https://faith.example.org"
```

Replace the example origin with your own verified public hostname. The private environment file is described below. After saving both files:

```bash
sudo systemctl daemon-reload
sudo systemctl restart fraternitas
sudo systemctl status fraternitas --no-pager
sudo systemctl show fraternitas -p EnvironmentFiles
```

The production deployment should use a stable private data directory (`FRATERNITAS_DATA_DIR`). Back up the SQLite database consistently (stop the service or use SQLite's backup mechanism), along with documents and configuration, to storage off the Pi. Never publish the private environment file or API key.

### Outbound email: Resend

Fraternitas has an administrator-only **Send test email** form under **Admin**. Set the recipient to your own email address. The Pi sends the request directly to Resend over HTTPS.

For production, set these server environment variables (for example in a protected
systemd `EnvironmentFile`, `/etc/fraternitas/resend.env`; **never commit the key to GitHub, appsettings.json, or source code**):

```dotenv
RESEND_API_KEY=YOUR_RESEND_API_KEY
FRATERNITAS_EMAIL_FROM="Fraternitas <notifications@mail.example.org>"
```

Verify your sending domain or dedicated subdomain in [Resend](https://resend.com/domains) using the DNS records it supplies. The sender must match that verified domain. Outbound sending does not require an inbound mailbox; leave existing mail-routing records intact.
For initial testing only, you can set
`FRATERNITAS_EMAIL_FROM="Fraternitas <onboarding@resend.dev>"`; Resend limits
its shared test sender to the email address associated with your Resend account.

After configuring environment variables and restarting Fraternitas, open
**Admin → Send test email**. The result page shows whether **Resend accepted**
the message and the Resend message ID. Check the inbox, spam folder, and Resend
dashboard for actual delivery. No mail is sent merely by visiting a page.

If Resend returns HTTP 403, inspect [Resend Logs](https://resend.com/logs). The default `resend.dev` sender is restricted to the Resend account owner's address. Change `FRATERNITAS_EMAIL_FROM` to your verified sending domain and restart the service; no rebuild is required. A real test email was received successfully on the Pi deployment on November 24, 2026. CI uses a mock provider instead of sending real mail.

The email service does not modify the database schema. Account requests use an additive table upgrade described below.

### Request an account

On the sign-in page, choose **Request an account** and enter a name, email address,
and optional note. Requests are stored separately from Identity accounts, so an
applicant cannot sign in or access private content before completing an approved registration.

Under **Admin → Account requests**, select **Approve & email registration link**.
All new accounts register as Member only; administrators may promote users after registration.
The applicant receives a single-use, seven-day link through Resend, using the configured
public URL. The applicant chooses their own password when completing registration;
the approved email address cannot be changed on that form. A copyable link is available
if Resend is unavailable. Resending replaces the old link, and **Delete request**
removes the application and revokes its link without deleting any existing member.

Shared group invitations also create Member-only accounts, including previously issued links.
Existing administrators retain their access. The new AccountRequests table is added automatically on startup using an
idempotent SQLite upgrade; existing members, calendar events, discussions, and documents
are preserved. Back up the persistent data directory before deploying as usual.

### Forgot password (automatic email)

On the sign-in page, select **Forgot your password?** and enter the registered email address.
When Resend is configured, Fraternitas emails the member a private reset link. The response
does not reveal whether an account exists. Automatic requests are limited to one email
attempt per member every ten minutes.

Administrators can also select **Reset password** next to any member under **Admin → Member access**.
The app attempts to email the link and displays a copyable link as a fallback, even if email
is unconfigured or rejected. The administrator can always generate another link.
The link expires after two hours, is single-use, and a replacement invalidates the previous link.
Opening it does not change the password until the member chooses a new one.

The reset request, cooldown, and hashed link use the existing `AspNetUserTokens` table.
No database migration is required. If no email arrives, check spam and ask an administrator
for help. Resend's shared test sender is limited to the Resend account email; verify your
sending domain before offering password recovery to every member. The user must have access
to their registered email address. Existing accounts were created without independent email
verification, so administrators should confirm any incorrect or outdated member addresses.

If the only administrator loses access and email is unavailable, use the local
`admin-setup` recovery command below.

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

To invite members, sign in as administrator, open **Admin**, create **one group invitation link**, then send that same URL to everyone in the group chat. Each person registers an individual account with it, receiving **Member access by default**. The link remains usable for additional registrations until it expires after seven days or you revoke it from **Admin**. Only the token hash is stored; copy the full URL before leaving the page. Invitations created in older versions and already redeemed stay unusable; generate a fresh shared link.

## Privacy and security

- The GitHub repository is public; **private group content belongs in the protected data directory, not the repository**. Do not commit private member records, credentials, reflections, or uploads.
- Calendar, readings, discussions and uploads are protected by authenticated routes. Downloads are protected endpoints, not public static paths.
- Both invitation and approved request registrations create ordinary **Members**. Administrators can promote members after registration. All members can view, create and pin calendar events, edit or delete their own events, and participate in group activities. Administrators may manage any event, invitations, account requests and roles. **Share reusable invitations carefully.**
- In **Admin → Member access**, select **Set member only** to downgrade an account, or **Make administrator** to restore it. The app prevents downgrading the last administrator. Role changes take effect when the user signs in again (and sensitive edit actions check the current database role).
- Startup no longer promotes members automatically. Existing administrators retain their access, and explicit Member-only exceptions are respected. A nullable `CreatedByUserId` calendar column is added automatically to existing SQLite databases; existing and seeded events without an owner remain administrator-managed. Back up the data directory before deploying.
- Passwords must be non-empty; length/complexity restrictions were intentionally removed for ease of use. Encourage strong unique passwords because group discussions and uploaded documents remain private. Passwords are hashed using ASP.NET Core Identity. Authentication requests, first-run setup and sign-out use antiforgery validation.
- The admin web setup is available only from loopback in Development before the first user exists; existing admin recovery requires local terminal access.
- Shared invite URLs are secret, reusable by multiple distinct members, expire after seven days and may be revoked. Approved public requests instead use a single-use link tied to the approved address. Older/shared-invite accounts do not have independent email verification.
- Uploads are stored outside `wwwroot`, with randomized storage names, extension allowlisting and a 10 MB limit. This is not an antivirus or content-scanning service.
- **Use HTTPS and secure cookie settings in production.** Do not expose a local HTTP development instance on the public internet.
- Operational priorities: monitored off-device backups, stronger abuse protection for public routes, secure production configuration, a full migration strategy and a file-scanning policy. Review data retention/privacy expectations with the group.

## Advent 2026 calendar (automatic, one-time seed)

On the first startup after this update, Fraternitas fills the calendar in **both new and existing SQLite databases**, without resetting member accounts or replacing manually scheduled events. A transaction and the `AppSeedHistory` marker prevent duplicate entries on subsequent launches and prevent manually deleted seeded events from reappearing. The marker lives in the existing SQLite database; no EF schema migration is required.

- **Monday gatherings, 8:00–9:00 PM Eastern**: Nov 23 (Annunciation, Luke 1:26–38), Nov 30 (Visitation, Luke 1:39–56), Dec 7 (Nativity, Luke 2:1–20), Dec 14 (Presentation, Luke 2:22–38), Dec 21 (Finding in the Temple, Luke 2:41–52).
- **Wednesday fast until 4:00 PM Eastern + meatless Wednesdays**: Nov 25, Dec 2, 9, 16 and 23.
- **Friday fast until 4:00 PM Eastern**: Nov 27, Dec 4, 11 and 18. Dec 25 (Christmas Day) is not included.
- **Daily personal practices**: all-day reminders Nov 23–Dec 24 for 20 minutes of silent prayer, the daily Ignatian Examen, and the agreed sacrifice of unnecessary phone/TV time. Their timing is chosen by each member; the calendar has a toggle to show/hide these reminders, hidden by default to keep the month view readable.

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
- **Pin on calendar**: any signed-in member can choose a date (Eastern) and create an all-day **Scripture** calendar event that includes the verse and reflection.
- **Pin in this discussion**: from an existing discussion, select **Pin a Bible verse in this discussion**. This opens the readings and adds the chosen verse as a reply to that thread.

Calendar and discussion pages both include shortcuts to the readings for pinning. Pins are saved into existing `CalendarEvent`, `Discussion` and `Reply` records; **no new SQLite migration is needed**. The Bible text is always independently fetched from the verified 73-book Douay–Rheims endpoint before saving. If the API is unavailable, pinning fails without creating an unverified quote. Personal notes are displayed as escaped text, not executable HTML.

The **Discussions** page and individual discussion threads include the full Catholic Bible browser. Choose any of the 73 books and a chapter, optionally enter a verse number, and use the compact right-aligned **📌 Pin** button. The browser supports navigating chapters; full-text Bible search is not implemented.

An integration test (`verify-scripture-pins`) uses an isolated SQLite database to verify that a Scripture topic, an existing-thread reply, and an all-day event persist correctly, including non-admin calendar pin creation, recorded ownership and anonymous rejection.
## Calendar and home page

Select a calendar appointment in day, week or month view to open its detail modal. All members can add events. Event creators and administrators can select **Edit event**, save changes or delete the event without leaving the calendar route. Events created before ownership tracking and seeded events can only be edited or deleted by administrators. The selected calendar view and date are retained. The home page queries the same SQLite records, so changes appear the next time the home page loads, if the event falls within its displayed week. Daily practice reminders are hidden by default and the calendar visibility preference persists per user.

## Build and test

```bash
dotnet restore Fraternitas/Fraternitas.csproj
dotnet build Fraternitas/Fraternitas.csproj -c Release --no-restore
```

[GitHub Actions](https://github.com/ARMoir/Fraternitas/actions/workflows/build.yml) runs on pushes to `main` and pull requests. It verifies the Catholic canon and Bible browser using the live HelloAO API, Scripture pin persistence, role defaults and exceptions, per-user preferences, mocked Resend reset emails, and native HTTP workflows for sign-in, private routes, calendar modals and home page synchronization, document uploads, password recovery fallback and account request approval/registration/revocation. A separate smoke test verifies first-run administrator setup, ordinary-member invitation registrations, and calendar creation, owner/admin editing and deletion, cross-member denial and protection of legacy events. Tests use isolated SQLite data and do not require production credentials or send real email. The live Scripture check requires network access to HelloAO.

For local verification, use a disposable data directory, not your production database:

```bash
export FRATERNITAS_DATA_DIR="$(mktemp -d)"
for check in verify-scripture verify-scripture-pins verify-member-access verify-user-preferences verify-password-reset-email; do
  dotnet run --no-launch-profile --project Fraternitas/Fraternitas.csproj -- "$check" || break
done
```

## Tech & roadmap

- `Fraternitas/Data`: EF/Identity models and initial seed
- `Fraternitas/Services`: account endpoints, invitation handling, local file storage
- `Fraternitas/Components/Pages`: calendar, readings, events, documents, discussions and administration
- `Fraternitas/wwwroot/app.css`: responsive visual theme
- `.github/workflows/build.yml` and `.github/scripts/native-actions-test.sh`: build and regression validation on push and pull request

Potential next stages: dependable backups, calendar recurrence and optional reminders, group-level configuration, stronger public-route abuse protection, and robust migrations. Keep devotional commitments private and optional.
