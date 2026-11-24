#!/usr/bin/env bash
# Runs on the wizard test server with two independently registered ordinary members.
set -euo pipefail
base=http://127.0.0.1:5089
owner="$RUNNER_TEMP/first.cookies"
other="$RUNNER_TEMP/second.cookies"
admin="$RUNNER_TEMP/wizard-cookies"
date=2026-11-23
calendar="$base/calendar?view=day&date=$date"

token() {
  python3 - "$1" "$2" <<'PY'
import sys
from html.parser import HTMLParser
class Token(HTMLParser):
    current = False
    token = None
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "form":
            self.current = a.get("action") == sys.argv[2]
        elif self.current and tag == "input" and a.get("name") == "__RequestVerificationToken":
            self.token = a.get("value")
    def handle_endtag(self, tag):
        if tag == "form":
            self.current = False
p = Token()
p.feed(open(sys.argv[1], encoding="utf-8").read())
assert p.token, "Missing form token"
print(p.token)
PY
}
get_calendar() {
  curl -sSf -b "$1" -c "$1" "$calendar" -o "$2"
  grep -q 'action="/calendar/events/create"' "$2"
}
get_calendar "$owner" "$RUNNER_TEMP/owner-cal.html"
get_calendar "$other" "$RUNNER_TEMP/other-cal.html"
get_calendar "$admin" "$RUNNER_TEMP/admin-cal.html"
owner_csrf=$(token "$RUNNER_TEMP/owner-cal.html" /calendar/events/create)
other_csrf=$(token "$RUNNER_TEMP/other-cal.html" /calendar/events/create)
admin_csrf=$(token "$RUNNER_TEMP/admin-cal.html" /calendar/events/create)

create() {
  curl -sSf -b "$owner" -o /dev/null \
    --data-urlencode "__RequestVerificationToken=$owner_csrf" \
    --data-urlencode "title=$1" --data-urlencode "category=Fellowship" \
    --data-urlencode "startLocal=${date}T10:00" --data-urlencode "endLocal=${date}T11:00" \
    --data-urlencode "CreatedByUserId=forged-owner" "$base/calendar/events/create"
}
event_id() {
  python3 - "$1" <<'PY'
import os, sqlite3, sys
db = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
row = db.execute("SELECT e.Id, u.Email FROM CalendarEvents e LEFT JOIN AspNetUsers u ON e.CreatedByUserId=u.Id WHERE e.Title=?", (sys.argv[1],)).fetchone()
assert row and row[1] == "first@example.invalid", row
print(row[0])
PY
}
create "Owner permission test"
id=$(event_id "Owner permission test")
# All members can view events; only the owner and admins get edit controls.
curl -sSf -b "$other" "$calendar&edit=$id" -o "$RUNNER_TEMP/other-view.html"
grep -q 'Owner permission test' "$RUNNER_TEMP/other-view.html"
if grep -q 'id="edit-event"' "$RUNNER_TEMP/other-view.html"; then exit 1; fi
curl -sSf -b "$owner" "$calendar&edit=$id" -o "$RUNNER_TEMP/owner-edit.html"
grep -q 'id="edit-event"' "$RUNNER_TEMP/owner-edit.html"

update() {
  curl -sS -b "$1" -D "$RUNNER_TEMP/permission-headers" -o /dev/null -w '%{http_code}' \
    --data-urlencode "__RequestVerificationToken=$2" --data-urlencode "title=$3" \
    --data-urlencode "category=Fellowship" --data-urlencode "startLocal=${date}T10:00" \
    --data-urlencode "endLocal=${date}T11:00" "$base/calendar/events/$4/update"
}
delete_event() {
  curl -sS -b "$1" -D "$RUNNER_TEMP/permission-headers" -o /dev/null -w '%{http_code}' \
    --data-urlencode "__RequestVerificationToken=$2" --data-urlencode "confirmDelete=yes" \
    "$base/calendar/events/$3/delete"
}
denied() {
  # Identity's cookie handler may redirect a forbidden member to AccessDenied.
  if [ "$1" = 403 ]; then return; fi
  test "$1" = 302
  grep -Eiq '^location: .*([Aa]ccess[Dd]enied|[Dd]enied)' "$RUNNER_TEMP/permission-headers"
}
denied "$(update "$other" "$other_csrf" "Unauthorized edit" "$id")"
denied "$(delete_event "$other" "$other_csrf" "$id")"
test "$(event_id "Owner permission test")" = "$id"
test "$(update "$owner" "$owner_csrf" "Owner edited event" "$id")" = 302
test "$(event_id "Owner edited event")" = "$id"
test "$(update "$admin" "$admin_csrf" "Admin edited event" "$id")" = 302
test "$(event_id "Admin edited event")" = "$id"
test "$(delete_event "$owner" "$owner_csrf" "$id")" = 302
create "Administrator deletion test"
id2=$(event_id "Administrator deletion test")
test "$(delete_event "$admin" "$admin_csrf" "$id2")" = 302
legacy=$(python3 - <<'PY'
import os, sqlite3
db = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
print(db.execute("SELECT Id FROM CalendarEvents WHERE CreatedByUserId IS NULL LIMIT 1").fetchone()[0])
PY
)
denied "$(update "$owner" "$owner_csrf" "Unauthorized legacy edit" "$legacy")"
denied "$(delete_event "$owner" "$owner_csrf" "$legacy")"
python3 - "$id" "$id2" "$legacy" <<'PY'
import os, sqlite3, sys
db = sqlite3.connect(os.path.join(os.environ["FRATERNITAS_DATA_DIR"], "fraternitas.db"))
for id in sys.argv[1:3]:
    assert not db.execute("SELECT 1 FROM CalendarEvents WHERE Id=?", (id,)).fetchone()
assert db.execute("SELECT 1 FROM CalendarEvents WHERE Id=? AND CreatedByUserId IS NULL", (sys.argv[3],)).fetchone()
PY
echo "Calendar permissions verified: member creation, owner/admin editing and deletion, cross-member and legacy protection"
