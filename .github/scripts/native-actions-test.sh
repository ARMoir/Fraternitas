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
