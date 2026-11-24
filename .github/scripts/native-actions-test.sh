#!/usr/bin/env bash
# Runs inside the existing authenticated smoke-test server after sign-in.
# Tests buttons/forms without JavaScript or a Blazor Server websocket.
set -euo pipefail

base="http://127.0.0.1:5088"
cookies="$RUNNER_TEMP/cookies.txt"

token_for_form() {
  python3 - "$1" "$2" <<'PY'
from html.parser import HTMLParser
import sys
class FormToken(HTMLParser):
    def __init__(self, action):
        super().__init__()
        self.action = action
        self.current = False
        self.token = None
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "form":
            self.current = a.get("action") == self.action
        elif tag == "input" and self.current and a.get("name") == "__RequestVerificationToken":
            self.token = a.get("value")
    def handle_endtag(self, tag):
        if tag == "form":
            self.current = False
parser = FormToken(sys.argv[2])
with open(sys.argv[1], encoding="utf-8") as file:
    parser.feed(file.read())
if not parser.token:
    raise SystemExit("No antiforgery token for " + sys.argv[2])
print(parser.token)
PY
}

# Reproduce the original desktop failure: POSTs complete without a circuit.
curl -sSf -b "$cookies" "$base/admin" -o "$RUNNER_TEMP/admin-form.html"
grep -q 'action="/admin/invitations/create"' "$RUNNER_TEMP/admin-form.html"
grep -q 'action="/admin/members/role"' "$RUNNER_TEMP/admin-form.html"
invite_csrf=$(token_for_form "$RUNNER_TEMP/admin-form.html" "/admin/invitations/create")
curl -sS -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$invite_csrf" \
  "$base/admin/invitations/create" -o "$RUNNER_TEMP/invitation-created.html"
grep -q 'Group invitation created' "$RUNNER_TEMP/invitation-created.html"
grep -q 'account/join?code=' "$RUNNER_TEMP/invitation-created.html"

# Check that only a hash, never the raw invitation code, was stored.
python3 - "$RUNNER_TEMP/invitation-created.html" <<'PY'
import os, sqlite3, sys
from html.parser import HTMLParser
from urllib.parse import urlsplit, parse_qs
class Input(HTMLParser):
    def __init__(self):
        super().__init__()
        self.url = None
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "input" and a.get("id") == "invite-url":
            self.url = a.get("value")
parser = Input()
parser.feed(open(sys.argv[1], encoding="utf-8").read())
assert parser.url, "No shareable invitation URL"
url = urlsplit(parser.url)
assert url.scheme in ("http", "https")
assert url.path == "/account/join"
token = parse_qs(url.query)["code"][0]
con = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
rows = con.execute("SELECT TokenSha256 FROM Invitations WHERE GroupId=1").fetchall()
import hashlib
assert hashlib.sha256(token.encode()).hexdigest().upper() in [row[0].upper() for row in rows]
assert token not in [row[0] for row in rows], "Invitation token stored as plaintext"
print("Native invitation creation verified; only SHA-256 token hash persisted")
PY

# Revoke the newly created link through a normal POST.
curl -sSf -b "$cookies" "$base/admin" -o "$RUNNER_TEMP/admin-revoke.html"
csrf=$(token_for_form "$RUNNER_TEMP/admin-revoke.html" "/admin/invitations/revoke")
invite_id=$(python3 - <<'PY'
import os, sqlite3
con = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
row = con.execute("SELECT Id FROM Invitations WHERE GroupId=1 AND RedeemedUtc IS NULL ORDER BY rowid DESC LIMIT 1").fetchone()
assert row
print(row[0])
PY
)
curl -sS -D "$RUNNER_TEMP/revoke-headers" -o /dev/null -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$csrf" \
  --data-urlencode "invitationId=$invite_id" \
  "$base/admin/invitations/revoke"
grep -qi 'location: /admin?status=invite-revoked' "$RUNNER_TEMP/revoke-headers"
python3 - "$invite_id" <<'PY'
import os, sqlite3, sys
con = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
row = con.execute("SELECT RedeemedUtc FROM Invitations WHERE lower(Id)=lower(?)", (sys.argv[1],)).fetchone()
assert row and row[0], "Invitation was not revoked in SQLite"
print("Native invitation revocation verified")
PY

# Discussion posting & reply both use native POST + member identity.
curl -sSf -b "$cookies" "$base/discussions" -o "$RUNNER_TEMP/discussion-form.html"
csrf=$(token_for_form "$RUNNER_TEMP/discussion-form.html" "/discussions/create")
curl -sS -D "$RUNNER_TEMP/new-discussion-headers" -o /dev/null -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$csrf" \
  --data-urlencode "title=Advent fellowship smoke test" \
  --data-urlencode "body=Discussing the Joyful Mysteries." \
  "$base/discussions/create"
topic=$(python3 - "$RUNNER_TEMP/new-discussion-headers" <<'PY'
import re, sys
headers = open(sys.argv[1], encoding="utf-8").read()
match = re.search(r"^location:\s*/discussions/([0-9a-f-]{36})", headers, re.I | re.M)
assert match, "Discussion creation did not redirect to its page"
print(match.group(1))
PY
)
curl -sSf -b "$cookies" "$base/discussions/$topic" -o "$RUNNER_TEMP/reply-form.html"
grep -q 'Advent fellowship smoke test' "$RUNNER_TEMP/reply-form.html"
csrf=$(token_for_form "$RUNNER_TEMP/reply-form.html" "/discussions/$topic/reply")
curl -sS -D "$RUNNER_TEMP/reply-headers" -o /dev/null -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$csrf" \
  --data-urlencode "message=Praying together as Catholic brothers." \
  "$base/discussions/$topic/reply"
grep -qi "location: /discussions/$topic" "$RUNNER_TEMP/reply-headers"
curl -sSf -b "$cookies" "$base/discussions/$topic" -o "$RUNNER_TEMP/replied.html"
grep -q 'Praying together as Catholic brothers.' "$RUNNER_TEMP/replied.html"
echo "Native discussion and reply workflows verified"

# The discussion Bible browser must work across the Catholic canon, not just Luke.
curl -sSf -b "$cookies" \
  "$base/discussions?book=JHN&chapter=3&verse=16" -o "$RUNNER_TEMP/bible-verse.html"
grep -q 'Browse the Holy Bible' "$RUNNER_TEMP/bible-verse.html"
grep -q 'name="book"' "$RUNNER_TEMP/bible-verse.html"
grep -q 'value="TOB"' "$RUNNER_TEMP/bible-verse.html"
grep -q 'Pin John 3:16' "$RUNNER_TEMP/bible-verse.html"
bible_csrf=$(token_for_form "$RUNNER_TEMP/bible-verse.html" "/scripture/pin")
curl -sS -D "$RUNNER_TEMP/bible-pin-headers" -o /dev/null -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$bible_csrf" \
  --data-urlencode "reference=JHN 3:16" \
  --data-urlencode "reflection=For God so loved the world." \
  --data-urlencode "mode=discussion" "$base/scripture/pin"
scripture_topic=$(python3 - "$RUNNER_TEMP/bible-pin-headers" <<'PY'
import re, sys
headers = open(sys.argv[1], encoding="utf-8").read()
match = re.search(r"^location:\s*/discussions/([0-9a-f-]{36})", headers, re.I | re.M)
assert match, "Selected John verse was not posted as a new discussion"
print(match.group(1))
PY
)
curl -sSf -b "$cookies" \
  "$base/discussions/$scripture_topic?book=TOB&chapter=1&verse=1" \
  -o "$RUNNER_TEMP/tobit-discussion.html"
grep -q 'Pin in this discussion' "$RUNNER_TEMP/tobit-discussion.html"
bible_csrf=$(token_for_form "$RUNNER_TEMP/tobit-discussion.html" "/scripture/pin")
curl -sS -D "$RUNNER_TEMP/tobit-reply-headers" -o /dev/null -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$bible_csrf" \
  --data-urlencode "reference=TOB 1:1" \
  --data-urlencode "discussionId=$scripture_topic" \
  --data-urlencode "reflection=From a different Catholic book." \
  --data-urlencode "mode=discussion" "$base/scripture/pin"
grep -qi "location: /discussions/$scripture_topic" "$RUNNER_TEMP/tobit-reply-headers"
python3 - "$scripture_topic" <<'PY'
import os, sqlite3, sys
con = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
topic = con.execute('SELECT Body FROM Discussions WHERE lower(Id)=lower(?)', (sys.argv[1],)).fetchone()
reply = con.execute('SELECT Body FROM Replies WHERE lower(DiscussionId)=lower(?) AND Body LIKE ?',
                    (sys.argv[1], '%TOB 1:1%')).fetchone()
assert topic and "JHN 3:16" in topic[0] and "Douay–Rheims" in topic[0]
assert reply and "TOB 1:1" in reply[0] and "Douay–Rheims" in reply[0]
print("Verse from John pinned as topic; verse from Tobit pinned as reply")
PY
echo "73-book Bible browser and discussion verse pins verified"


# The Scripture pin endpoint also requires authentication and CSRF protection.
curl -sS -D "$RUNNER_TEMP/pin-invalid-headers" -o /dev/null -b "$cookies" \
  --data-urlencode "reference=Luke 1:26" \
  --data-urlencode "mode=discussion" \
  "$base/scripture/pin"
grep -qE '^HTTP/[^ ]+ 400' "$RUNNER_TEMP/pin-invalid-headers"
echo "Scripture pin endpoint rejects missing antiforgery token"

# Ensure an anonymous client cannot create an invitation.
status=$(curl -sS -o /dev/null -w '%{http_code}' \
  --data-urlencode "anything=1" "$base/admin/invitations/create")
test "$status" = "302" || test "$status" = "401" || test "$status" = "403"
echo "Admin-only invitation creation rejects anonymous POST"

# Password recovery pages and administrator controls must render over plain HTTP.
curl -sSf -b "$cookies" "$base/account/login" -o "$RUNNER_TEMP/recovery-login.html"
grep -q 'Forgot your password?' "$RUNNER_TEMP/recovery-login.html"
curl -sSf -b "$cookies" "$base/account/forgot-password" -o "$RUNNER_TEMP/recovery-request.html"
grep -q 'action="/account/forgot-password"' "$RUNNER_TEMP/recovery-request.html"
grep -q 'Request password reset' "$RUNNER_TEMP/recovery-request.html"
curl -sSf -b "$cookies" "$base/admin" -o "$RUNNER_TEMP/recovery-admin.html"
grep -q 'action="/admin/members/password-reset"' "$RUNNER_TEMP/recovery-admin.html"
echo "Forgot-password and administrator recovery controls rendered"

# Admin-only Resend test-send must show a clear unconfigured state in CI.
curl -sSf -b "$cookies" "$base/admin" -o "$RUNNER_TEMP/email-test-admin.html"
grep -q 'action="/admin/email/test"' "$RUNNER_TEMP/email-test-admin.html"
grep -q 'value="alex@moir.pw"' "$RUNNER_TEMP/email-test-admin.html"
email_csrf=$(token_for_form "$RUNNER_TEMP/email-test-admin.html" "/admin/email/test")
curl -sSf -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$email_csrf" \
  --data-urlencode "recipient=alex@moir.pw" \
  "$base/admin/email/test" -o "$RUNNER_TEMP/email-test-result.html"
grep -q 'Test email not sent' "$RUNNER_TEMP/email-test-result.html"
grep -q "RESEND_API_KEY" "$RUNNER_TEMP/email-test-result.html"
echo "Resend test UI and missing-key handling verified"


# Forgot password remains usable when a deployment has not configured Resend.
# Use only the disposable CI database and its test account.
curl -sSf -c "$RUNNER_TEMP/forgot.cookies" \
  "$base/account/forgot-password" -o "$RUNNER_TEMP/forgot-form.html"
forgot_csrf=$(token_for_form "$RUNNER_TEMP/forgot-form.html" "/account/forgot-password")
curl -sSf -b "$RUNNER_TEMP/forgot.cookies" -c "$RUNNER_TEMP/forgot.cookies" \
  --data-urlencode "__RequestVerificationToken=$forgot_csrf" \
  --data-urlencode "email=$FRATERNITAS_ADMIN_EMAIL" \
  "$base/account/forgot-password" -o "$RUNNER_TEMP/forgot-response.html"
grep -q 'Request received' "$RUNNER_TEMP/forgot-response.html"
grep -q 'password reset link will be emailed' "$RUNNER_TEMP/forgot-response.html"
curl -sSf -b "$cookies" "$base/admin" -o "$RUNNER_TEMP/reset-admin.html"
grep -q 'Reset requested' "$RUNNER_TEMP/reset-admin.html"
reset_admin_csrf=$(token_for_form "$RUNNER_TEMP/reset-admin.html" "/admin/members/password-reset")
test_user=$(python3 - <<'PY'
import os, sqlite3
db = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
row = db.execute("SELECT Id FROM AspNetUsers WHERE Email=?", (os.environ["FRATERNITAS_ADMIN_EMAIL"],)).fetchone()
assert row, "CI test member missing"
print(row[0])
PY
)
curl -sSf -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$reset_admin_csrf" \
  --data-urlencode "userId=$test_user" \
  "$base/admin/members/password-reset" -o "$RUNNER_TEMP/reset-fallback.html"
grep -q 'Email delivery is not configured' "$RUNNER_TEMP/reset-fallback.html"
grep -q 'id="reset-url"' "$RUNNER_TEMP/reset-fallback.html"
echo "Forgot password requests and administrator link fallback verified with no Resend key"


# Public requests must not create accounts or grant access until approved registration.
# The CI database and example.invalid recipients are disposable; no Resend key is set.
curl -sSf -c "$RUNNER_TEMP/request.cookies" "$base/account/request" -o "$RUNNER_TEMP/account-request.html"
request_csrf=$(token_for_form "$RUNNER_TEMP/account-request.html" "/account/request")
for attempt in 1 2; do
  curl -sSf -b "$RUNNER_TEMP/request.cookies" \
    --data-urlencode "__RequestVerificationToken=$request_csrf" \
    --data-urlencode "name=CI Applicant" \
    --data-urlencode "email=ci-applicant@example.invalid" \
    --data-urlencode "note=Integration test only" \
    "$base/account/request" -o "$RUNNER_TEMP/request-result.html"
  grep -q 'Request received' "$RUNNER_TEMP/request-result.html"
done
request_id=$(python3 - <<'PY'
import os, sqlite3
db = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
rows = db.execute("SELECT Id FROM AccountRequests WHERE Email=?", ("ci-applicant@example.invalid",)).fetchall()
assert len(rows) == 1, "Duplicate account requests not deduplicated"
assert not db.execute("SELECT Id FROM AspNetUsers WHERE Email=?", ("ci-applicant@example.invalid",)).fetchone(), "Unapproved applicant has an account"
print(rows[0][0])
PY
)
curl -sSf -b "$cookies" "$base/admin" -o "$RUNNER_TEMP/request-admin.html"
grep -q 'CI Applicant' "$RUNNER_TEMP/request-admin.html"
grep -q 'Awaiting approval' "$RUNNER_TEMP/request-admin.html"
approve_csrf=$(token_for_form "$RUNNER_TEMP/request-admin.html" "/admin/account-requests/approve")
curl -sSf -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$approve_csrf" \
  --data-urlencode "requestId=$request_id" \
  --data-urlencode "grantAdmin=false" \
  "$base/admin/account-requests/approve" -o "$RUNNER_TEMP/request-approved.html"
grep -q 'Account request approved' "$RUNNER_TEMP/request-approved.html"
grep -q 'You can still share the private link directly' "$RUNNER_TEMP/request-approved.html"
registration_path=$(python3 - "$RUNNER_TEMP/request-approved.html" <<'PY'
import sys
from html.parser import HTMLParser
from urllib.parse import urlsplit
class RegistrationLink(HTMLParser):
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "input" and a.get("id") == "registration-url":
            parsed = urlsplit(a["value"])
            assert parsed.path == "/account/complete-registration"
            print(parsed.path + "?" + parsed.query)
parser = RegistrationLink()
parser.feed(open(sys.argv[1], encoding="utf-8").read())
PY
)
test -n "$registration_path"
curl -sSf -b "$RUNNER_TEMP/request.cookies" -c "$RUNNER_TEMP/request.cookies" \
  "$base$registration_path" -o "$RUNNER_TEMP/registration-form.html"
grep -q 'Complete your registration' "$RUNNER_TEMP/registration-form.html"
grep -q 'ci-applicant@example.invalid' "$RUNNER_TEMP/registration-form.html"
# Delete an approved request and verify the previously issued link is revoked.
curl -sSf -b "$cookies" "$base/admin" -o "$RUNNER_TEMP/request-delete-admin.html"
delete_csrf=$(token_for_form "$RUNNER_TEMP/request-delete-admin.html" "/admin/account-requests/delete")
curl -sS -D "$RUNNER_TEMP/request-delete-headers" -o /dev/null -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$delete_csrf" \
  --data-urlencode "requestId=$request_id" "$base/admin/account-requests/delete"
grep -qi 'location: /admin?status=request-deleted' "$RUNNER_TEMP/request-delete-headers"
curl -sSf -b "$RUNNER_TEMP/request.cookies" "$base$registration_path" -o "$RUNNER_TEMP/revoked-registration.html"
grep -q 'Registration link unavailable' "$RUNNER_TEMP/revoked-registration.html"
echo "Account request deduplication, approval, email fallback, and revocation verified"


# Reapply and finish a fresh approved registration using the local test server.
curl -sSf -b "$RUNNER_TEMP/request.cookies" -c "$RUNNER_TEMP/request.cookies" \
  "$base/account/request" -o "$RUNNER_TEMP/account-request-again.html"
request_csrf=$(token_for_form "$RUNNER_TEMP/account-request-again.html" "/account/request")
curl -sSf -b "$RUNNER_TEMP/request.cookies" \
  --data-urlencode "__RequestVerificationToken=$request_csrf" \
  --data-urlencode "name=CI Applicant" --data-urlencode "email=ci-applicant@example.invalid" \
  "$base/account/request" -o "$RUNNER_TEMP/request-again.html"
request_id=$(python3 - <<'PY'
import os, sqlite3
db = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
print(db.execute("SELECT Id FROM AccountRequests WHERE Email=?", ("ci-applicant@example.invalid",)).fetchone()[0])
PY
)
curl -sSf -b "$cookies" "$base/admin" -o "$RUNNER_TEMP/request-admin-again.html"
approve_csrf=$(token_for_form "$RUNNER_TEMP/request-admin-again.html" "/admin/account-requests/approve")
curl -sSf -b "$cookies" \
  --data-urlencode "__RequestVerificationToken=$approve_csrf" \
  --data-urlencode "requestId=$request_id" --data-urlencode "grantAdmin=false" \
  "$base/admin/account-requests/approve" -o "$RUNNER_TEMP/request-approved-again.html"
registration_code=$(python3 - "$RUNNER_TEMP/request-approved-again.html" <<'PY'
import sys
from html.parser import HTMLParser
from urllib.parse import urlsplit, parse_qs
class RegistrationLink(HTMLParser):
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "input" and a.get("id") == "registration-url":
            print(parse_qs(urlsplit(a["value"]).query)["code"][0])
RegistrationLink().feed(open(sys.argv[1], encoding="utf-8").read())
PY
)
test -n "$registration_code"
curl -sSf -b "$RUNNER_TEMP/request.cookies" -c "$RUNNER_TEMP/request.cookies" \
  "$base/account/complete-registration?code=$registration_code" -o "$RUNNER_TEMP/complete-registration.html"
registration_csrf=$(token_for_form "$RUNNER_TEMP/complete-registration.html" "/account/complete-registration")
curl -sS -D "$RUNNER_TEMP/complete-registration-headers" -o /dev/null \
  -b "$RUNNER_TEMP/request.cookies" -c "$RUNNER_TEMP/request.cookies" \
  --data-urlencode "__RequestVerificationToken=$registration_csrf" \
  --data-urlencode "code=$registration_code" --data-urlencode "name=CI Applicant" \
  --data-urlencode "password=CI-only-registration-password" \
  --data-urlencode "confirm=CI-only-registration-password" \
  "$base/account/complete-registration"
tr -d '\r' < "$RUNNER_TEMP/complete-registration-headers" > "$RUNNER_TEMP/registration-normalized-headers"
grep -qi '^location: /$' "$RUNNER_TEMP/registration-normalized-headers"
python3 - <<'PY'
import os, sqlite3
db = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
uid, confirmed = db.execute("SELECT Id, EmailConfirmed FROM AspNetUsers WHERE Email=?", ("ci-applicant@example.invalid",)).fetchone()
assert confirmed == 1
roles = db.execute("SELECT r.Name FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE ur.UserId=?", (uid,)).fetchall()
assert roles == [("Member",)], roles
assert db.execute("SELECT 1 FROM AspNetUserClaims WHERE UserId=? AND ClaimType='Fraternitas.MemberOnly' AND ClaimValue='true'", (uid,)).fetchone()
assert not db.execute("SELECT 1 FROM AccountRequests WHERE Email=?", ("ci-applicant@example.invalid",)).fetchone()
PY
curl -sSf -b "$RUNNER_TEMP/request.cookies" \
  "$base/account/complete-registration?code=$registration_code" -o "$RUNNER_TEMP/registration-used.html"
grep -q 'Registration link unavailable' "$RUNNER_TEMP/registration-used.html"
echo "Approved member registration, access selection and single-use link verified"
