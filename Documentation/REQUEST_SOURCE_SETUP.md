# Game Request Source Setup

The request flow uses Supabase Edge Functions as the only callers of Hubcap Manifest and lua.tools. Upstream credentials are stored in a private Supabase table and are not returned to regular game-request clients.

## Supabase setup

1. Run `Database/create_request_source_credentials_table.sql` in the Supabase SQL Editor. It migrates any existing `hubcap_api_key` row from the publicly readable `app_remote_config` table into private storage, then removes the public row.
2. Deploy the two Edge Functions from the repository root:

   ```powershell
   supabase functions deploy request-source-credentials
   supabase functions deploy request-game-assets
   ```

3. Ensure the Supabase project has the standard `SUPABASE_URL` and `SUPABASE_SERVICE_ROLE_KEY` Edge Function secrets. `HUBCAP_BASE_URL` is optional and defaults to `https://hubcapmanifest.com/api/v1`.
4. Configure the Game Request Discord webhook in the app's service configuration.

The credential function verifies the requesting device against `user_profiles` and only permits profiles whose `level` is `0`. The game asset function requires a registered, unblocked device profile. The private credential table has no `anon` or `authenticated` access policy; only the Edge Functions use the service role to read it.

## Admin credential updates

Open DevView's **Request Sources** tab and use **Reload** or **Save Credentials**. Enter the Hubcap API key and either the lua.tools cookie header value or the full Netscape cookie export. Do not include a `Cookie:` header name. Netscape imports are filtered to active cookies matching lua.tools and the manifest download path. Empty or unmatched values can be saved, but requests requiring that credential will stop and notify the admin.

Credential values are never written to the client log. The source request function uses the Hubcap API key to validate Hubcap access and detect Denuvo, then downloads Denuvo games from Hubcap Manifest or other games from lua.tools.

## Request outcomes

- An existing AppID request is rejected before Steam validation or source calls.
- An upstream `401` or `403` for a credential stops the request and sends an admin alert through the Game Request webhook.
- An upstream `404` sends one Discord request message containing both the missing-source result and the game request details.
- Network errors and upstream `5xx` responses stop the request without being reported as expired credentials.
