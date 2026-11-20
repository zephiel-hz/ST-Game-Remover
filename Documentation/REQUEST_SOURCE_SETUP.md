# Game Request Source Setup

The request flow uses Supabase Edge Functions as the only callers of Hubcap Manifest and lua.tools. Upstream credentials are stored in a private Supabase table and are not returned to regular game-request clients.

## Supabase setup

1. Run the updated `Database/create_request_source_credentials_table.sql` in the Supabase SQL Editor. It migrates any existing `hubcap_api_key` row from the publicly readable `app_remote_config` table into private storage, initializes Account 1 from that key, and allows separate Account 1 and Account 2 keys.
2. Deploy the two Edge Functions from the repository root:

   ```powershell
   supabase functions deploy request-source-credentials
   supabase functions deploy request-game-assets
   ```

3. Ensure the Supabase project has the standard `SUPABASE_URL` and `SUPABASE_SERVICE_ROLE_KEY` Edge Function secrets. `HUBCAP_BASE_URL` is optional and defaults to `https://hubcapmanifest.com/api/v1`.
4. Configure the Game Request Discord webhook in the app's service configuration.

The credential function verifies the requesting device against `user_profiles` and only permits profiles whose `level` is `0`. The game asset function requires a registered, unblocked device profile. The private credential table has no `anon` or `authenticated` access policy; only the Edge Functions use the service role to read it.

## Admin credential updates

Open DevView's **Request Sources** tab. Each Hubcap account has its own **Generate API key** or **Regenerate API key** action and isolated Discord WebView profile. Sign in to the intended Discord account in each profile. Generated keys are saved to private Supabase storage automatically. Account 1 is tried first; Account 2 is the fallback if Account 1's key is rejected, rate-limited, or denied access to that game's files. Key regeneration is started per account from DevView and is not triggered automatically by game requests.

Use **Save Credentials** for manual key edits or the lua.tools cookie. Enter either the lua.tools cookie header value or the full Netscape cookie export. Do not include a `Cookie:` header name. Netscape imports are filtered to active cookies matching lua.tools and the manifest download path. Empty or unmatched values can be saved, but requests requiring that credential will stop and notify the admin.

Credential values are never written to the client log. The source request function validates the configured Hubcap accounts and uses the first account that can access the requested game to detect Denuvo and download Denuvo game files. Other games are downloaded from lua.tools first; if lua.tools returns no file, the function tries the configured Hubcap accounts in order for a Lua file. An invalid or expired lua.tools cookie stops the request and notifies the admin instead of falling back.

## Request outcomes

- An existing AppID request is rejected before Steam validation or source calls.
- An upstream `401` or `403` from both configured Hubcap keys stops the request and sends an admin alert through the Game Request webhook. A game-specific access denial is reported separately from invalid keys.
- An upstream `404` sends one Discord request message containing both the missing-source result and the game request details.
- Network errors and upstream `5xx` responses stop the request without being reported as expired credentials.
