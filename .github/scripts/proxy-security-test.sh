#!/usr/bin/env bash
set -euo pipefail
export ASPNETCORE_ENVIRONMENT=Production
export FRATERNITAS_DATA_DIR="$RUNNER_TEMP/proxy-security-data"
# Bind to a non-loopback address too ONLY in CI, to test rejecting its headers.
export ASPNETCORE_URLS=http://0.0.0.0:5090
email=proxy-test@example.invalid
password=ci-proxy-test-password1
printf '%s\n%s\n%s\n' "$email" "$password" "$password" | dotnet run --no-build --no-launch-profile -c Release --project Fraternitas/Fraternitas.csproj -- admin-setup
dotnet run --no-build --no-launch-profile -c Release --project Fraternitas/Fraternitas.csproj > "$RUNNER_TEMP/proxy-security.log" 2>&1 &
pid=$!
trap 'rc=$?; if [ "$rc" -ne 0 ]; then tail -100 "$RUNNER_TEMP/proxy-security.log"; fi; kill "$pid" 2>/dev/null || true' EXIT
base=http://127.0.0.1:5090
headers="$RUNNER_TEMP/proxy-headers"
form="$RUNNER_TEMP/proxy-login.html"
ready=0
for i in {1..40}; do
  if curl --noproxy '*' -sSf -H 'X-Forwarded-Proto: https' -D "$headers" "$base/account/login" -o "$form"; then ready=1; break; fi
  sleep 1
done
test "$ready" = 1
grep -q 'Welcome back' "$form"
# A local HTTP origin connection is HTTPS from the visitor's perspective.
grep -Eiq '^set-cookie: \.AspNetCore\.Antiforgery\..*; secure' "$headers"
csrf_cookie=$(sed -nE 's/^[Ss]et-[Cc]ookie: ([^;]+);.*/\1/p' "$headers" | head -1)
csrf=$(sed -n 's/.*name="__RequestVerificationToken" value="\([^"]*\)".*/\1/p' "$form" | head -1)
test -n "$csrf_cookie"
test -n "$csrf"
# Explicitly replay cookies to simulate the browser-to-Cloudflare HTTPS hop.
curl --noproxy '*' -sS -H 'X-Forwarded-Proto: https' -H "Cookie: $csrf_cookie" \
  -D "$headers" -o /dev/null --data-urlencode "__RequestVerificationToken=$csrf" \
  --data-urlencode "email=$email" --data-urlencode "password=$password" "$base/account/login"
grep -Eiq '^location: /[[:space:]]*$' "$headers"
grep -Eiq '^set-cookie: \.AspNetCore\.Identity\.Application=.*; secure' "$headers"
grep -Eiq '^set-cookie: \.AspNetCore\.Identity\.Application=.*; httponly' "$headers"
auth_cookie=$(sed -nE 's/^[Ss]et-[Cc]ookie: (\.AspNetCore\.Identity\.Application=[^;]+);.*/\1/p' "$headers" | head -1)
test -n "$auth_cookie"
curl --noproxy '*' -sSf -H 'X-Forwarded-Proto: https' -H "Cookie: $auth_cookie" "$base/calendar" -o "$RUNNER_TEMP/proxy-calendar.html"
grep -q 'Our shared calendar' "$RUNNER_TEMP/proxy-calendar.html"
# Without HTTPS or a trusted proxy scheme, production redirects before login.
curl --noproxy '*' -sS -D "$headers" -o /dev/null "$base/account/login"
grep -Eiq '^location: https://' "$headers"
# A remote peer cannot claim HTTPS merely by adding the same header.
nonloopback=$(hostname -I | awk '{print $1}')
test -n "$nonloopback"
curl --noproxy '*' -sS -H 'X-Forwarded-Proto: https' -D "$headers" -o /dev/null "http://$nonloopback:5090/account/login"
grep -Eiq '^location: https://' "$headers"
echo 'Production proxy scheme, secure authentication and antiforgery cookies, authenticated calendar and untrusted-header rejection verified.'
