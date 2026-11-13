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
    console.error("Device identity function is missing Supabase server configuration.");
    return jsonResponse(500, { error: "Device identity service is not configured." });
  }

  try {
    const body = await request.json();
    const tpmHash = typeof body.tpmHash === "string" ? body.tpmHash.toLowerCase() : "";
    const currentDeviceId =
      typeof body.currentDeviceId === "string" ? body.currentDeviceId.trim() : "";

    if (!/^[a-f0-9]{64}$/.test(tpmHash) ||
      currentDeviceId.length < 4 ||
      currentDeviceId.length > 256) {
      return jsonResponse(400, { error: "Invalid device identity." });
    }

    const admin = createClient(supabaseUrl, serviceRoleKey, {
      auth: { autoRefreshToken: false, persistSession: false },
    });

    const { data: alias, error: lookupError } = await admin
      .from("device_tpm_aliases")
      .select("device_id")
      .eq("tpm_hash", tpmHash)
      .maybeSingle();

    if (lookupError) {
      console.error("Could not look up TPM identity alias:", lookupError.message);
      return jsonResponse(503, { error: "Could not resolve device identity." });
    }

    if (alias) {
      return jsonResponse(200, { deviceId: alias.device_id, resolved: true });
    }

    const { data: linkedDeviceId, error: linkError } = await admin.rpc(
      "link_device_tpm_identity",
      { p_tpm_hash: tpmHash, p_device_id: currentDeviceId },
    );

    if (linkError) {
      const { data: previousAlias, error: previousAliasError } = await admin
        .from("device_tpm_aliases")
        .select("tpm_hash")
        .eq("device_id", currentDeviceId)
        .maybeSingle();

      if (!previousAliasError && previousAlias &&
        previousAlias.tpm_hash.toLowerCase() !== tpmHash) {
        return jsonResponse(200, {
          deviceId: `TPM-SHA256:${tpmHash.toUpperCase()}`,
          resolved: true,
        });
      }

      console.error("Could not link TPM identity:", linkError.message);
      return jsonResponse(409, { error: "Could not link device identity." });
    }

    if (typeof linkedDeviceId === "string" && linkedDeviceId.length > 0) {
      return jsonResponse(200, { deviceId: linkedDeviceId, resolved: true });
    }

    return jsonResponse(200, { deviceId: currentDeviceId, resolved: false });
  } catch (error) {
    console.error("Device identity request failed:", error);
    return jsonResponse(400, { error: "Invalid device identity request." });
  }
});
