import { createClient } from "npm:@supabase/supabase-js@2";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

const jsonResponse = (status: number, body: Record<string, unknown>) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { ...corsHeaders, "Content-Type": "application/json" },
  });

Deno.serve(async (request: Request) => {
  if (request.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }
  if (request.method !== "POST") {
    return jsonResponse(405, { error: "Method not allowed." });
  }

  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!supabaseUrl || !serviceRoleKey) {
    console.error("Request source credential function is missing Supabase configuration.");
    return jsonResponse(503, { error: "Credential service is not configured." });
  }

  let body: {
    action?: unknown;
    deviceId?: unknown;
    hubcapApiKey?: unknown;
    luaToolsCookie?: unknown;
  };
  try {
    body = await request.json();
  } catch {
    return jsonResponse(400, { error: "Invalid request body." });
  }

  const action = typeof body.action === "string" ? body.action : "";
  const deviceId = typeof body.deviceId === "string" ? body.deviceId.trim() : "";
  if (!["load", "save"].includes(action) || deviceId.length < 4 || deviceId.length > 256) {
    return jsonResponse(400, { error: "Invalid credential request." });
  }

  const admin = createClient(supabaseUrl, serviceRoleKey, {
    auth: { autoRefreshToken: false, persistSession: false },
  });

  const { data: profile, error: profileError } = await admin
    .from("user_profiles")
    .select("level")
    .eq("device_id", deviceId)
    .maybeSingle();

  if (profileError) {
    console.error("Could not verify credential administrator:", profileError.message);
    return jsonResponse(503, { error: "Could not verify administrator access." });
  }
  if (!profile || profile.level !== 0) {
    return jsonResponse(403, { error: "Administrator access is required." });
  }

  if (action === "load") {
    const { data, error } = await admin
      .from("request_source_credentials")
      .select("key,value")
      .in("key", ["hubcap_api_key", "lua_tools_cookie"]);

    if (error) {
      console.error("Could not load request source credentials:", error.message);
      return jsonResponse(503, { error: "Could not load request source credentials." });
    }

    const values = new Map((data ?? []).map((row) => [row.key, row.value]));
    return jsonResponse(200, {
      hubcapApiKey: values.get("hubcap_api_key") ?? "",
      luaToolsCookie: values.get("lua_tools_cookie") ?? "",
    });
  }

  const hubcapApiKey = body.hubcapApiKey;
  const luaToolsCookie = body.luaToolsCookie;
  if (typeof hubcapApiKey !== "string" ||
    typeof luaToolsCookie !== "string" ||
    hubcapApiKey.length > 16384 ||
    luaToolsCookie.length > 16384 ||
    /[\r\n]/.test(hubcapApiKey)) {
    return jsonResponse(400, { error: "Invalid credential values." });
  }

  const now = new Date().toISOString();
  const rows = [
    {
      key: "hubcap_api_key",
      value: hubcapApiKey,
      updated_at: now,
      updated_by_device_id: deviceId,
    },
    {
      key: "lua_tools_cookie",
      value: luaToolsCookie,
      updated_at: now,
      updated_by_device_id: deviceId,
    },
  ];

  const { error } = await admin
    .from("request_source_credentials")
    .upsert(rows, { onConflict: "key" });

  if (error) {
    console.error("Could not save request source credentials:", error.message);
    return jsonResponse(503, { error: "Could not save request source credentials." });
  }

  return jsonResponse(200, { saved: true });
});
