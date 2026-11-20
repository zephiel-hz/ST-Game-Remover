import { createClient, type SupabaseClient } from "npm:@supabase/supabase-js@2";

type UpstreamFile = {
  fileName: string;
  contentType: string;
  content: Uint8Array;
};

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

const safeFileName = (value: string, fallback: string): string => {
  const name = value.replace(/[\r\n"]/g, "").split(/[\\/]/).pop()?.trim() ?? "";
  return name && name !== "." && name !== ".." ? name : fallback;
};

const fileNameFromResponse = (response: Response, fallback: string): string => {
  const disposition = response.headers.get("content-disposition") ?? "";
  const match = /filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(disposition);
  if (!match) return fallback;
  let name = match[1].replace(/"$/, "");
  try {
    name = decodeURIComponent(name);
  } catch {
    // Keep the header value if it is not percent-encoded.
  }
  return safeFileName(name, fallback);
};

const errorResponse = (status: number, code: string, source: string, error: string) =>
  jsonResponse(status, { code, source, error });

const luaToolsCookieHeader = (storedCookie: string): string => {
  const lines = storedCookie.split(/\r?\n/);
  const isNetscapeFile = lines.some((line) =>
    line.startsWith("# Netscape HTTP Cookie File") ||
    line.startsWith("# HTTP Cookie File") ||
    line.split("\t").length === 7
  );
  if (!isNetscapeFile) {
    if (/[\r\n]/.test(storedCookie)) {
      throw new Error("Cookie header must be a single line.");
    }
    return storedCookie.trim();
  }

  const requestHost = "lua.tools";
  const requestPath = "/api/manifest/download";
  const nowSeconds = Math.floor(Date.now() / 1000);
  const cookies: string[] = [];

  for (const line of lines) {
    if (!line || line.startsWith("#") && !line.startsWith("#HttpOnly_")) continue;
    let fields = line.split("\t");
    if (fields.length !== 7) {
      const whitespaceFields =
        /^(\S+)\s+(TRUE|FALSE)\s+(\S+)\s+(TRUE|FALSE)\s+(\d+)\s+(\S+)\s+(.+)$/i.exec(line);
      if (!whitespaceFields) continue;
      fields = whitespaceFields.slice(1);
    }

    let domain = fields[0];
    if (domain.startsWith("#HttpOnly_")) domain = domain.slice("#HttpOnly_".length);
    const includeSubdomains = fields[1].toUpperCase() === "TRUE";
    domain = domain.replace(/^\./, "").toLowerCase();
    const domainMatches = requestHost === domain ||
      includeSubdomains && requestHost.endsWith(`.${domain}`);
    const cookiePath = fields[2];
    const pathMatches = cookiePath.startsWith("/") &&
      (requestPath === cookiePath ||
        requestPath.startsWith(cookiePath.endsWith("/") ? cookiePath : `${cookiePath}/`));
    const expires = Number(fields[4]);
    const isExpired = Number.isFinite(expires) && expires !== 0 && expires <= nowSeconds;
    const name = fields[5];
    const value = fields[6];

    if (!domainMatches || !pathMatches || isExpired ||
      !/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name) ||
      /[;\r\n]/.test(value)) {
      continue;
    }
    cookies.push(`${name}=${value}`);
  }

  if (cookies.length === 0) {
    throw new Error("Netscape cookie file has no active cookies for lua.tools.");
  }
  return cookies.join("; ");
};

const multipartResponse = (files: UpstreamFile[], isDenuvo: boolean, source: string) => {
  const boundary = `hz-lua-manager-${crypto.randomUUID()}`;
  const encoder = new TextEncoder();
  const parts: BlobPart[] = [];

  for (const file of files) {
    const contentType = file.contentType.replace(/[\r\n]/g, "").trim() ||
      "application/octet-stream";
    parts.push(encoder.encode(
      `--${boundary}\r\n` +
        `Content-Type: ${contentType}\r\n` +
        `Content-Disposition: attachment; filename="${safeFileName(file.fileName, "download")}"\r\n\r\n`,
    ));
    parts.push(new Uint8Array(file.content).buffer);
    parts.push(encoder.encode("\r\n"));
  }
  parts.push(encoder.encode(`--${boundary}--\r\n`));

  return new Response(new Blob(parts), {
    status: 200,
    headers: {
      ...corsHeaders,
      "Access-Control-Expose-Headers": "X-Game-Denuvo, X-Game-Source",
      "Content-Type": `multipart/mixed; boundary=${boundary}`,
      "X-Game-Denuvo": String(isDenuvo),
      "X-Game-Source": source,
    },
  });
};

const getCredential = async (
  admin: SupabaseClient<any, "public", "public", any, any>,
  key: string,
): Promise<string> => {
  const { data, error } = await admin
    .from("request_source_credentials")
    .select("value")
    .eq("key", key)
    .maybeSingle();
  if (error) throw new Error(`Could not load ${key} from private configuration.`);
  return typeof data?.value === "string" ? data.value.trim() : "";
};

const getHubcapApiKeys = async (
  admin: SupabaseClient<any, "public", "public", any, any>,
): Promise<Array<{ accountNumber: 1 | 2; apiKey: string }>> => {
  const { data, error } = await admin
    .from("request_source_credentials")
    .select("key,value")
    .in("key", ["hubcap_api_key_1", "hubcap_api_key_2", "hubcap_api_key"]);
  if (error) throw new Error("Could not load Hubcap API keys from private configuration.");

  const values = new Map((data ?? []).map((row) => [row.key, row.value]));
  const primaryKey = values.get("hubcap_api_key_1") || values.get("hubcap_api_key") || "";
  const secondaryKey = values.get("hubcap_api_key_2") || "";
  const credentials: Array<{ accountNumber: 1 | 2; apiKey: string }> = [];
  if (primaryKey.trim())
    credentials.push({ accountNumber: 1, apiKey: primaryKey.trim() });
  if (secondaryKey.trim() && secondaryKey.trim() !== primaryKey.trim())
    credentials.push({ accountNumber: 2, apiKey: secondaryKey.trim() });
  return credentials;
};

const hubcapHeaders = (apiKey: string) => ({
  Authorization: `Bearer ${apiKey}`,
  "X-API-Key": apiKey,
  "User-Agent": "HZLuaManager/1.0",
});

const detectDenuvo = async (appId: number, apiKey: string): Promise<boolean | null> => {
  try {
    const response = await fetch(
      `https://hubcapmanifest.com/manifest-history/api/apps/${appId}/drm`,
      { headers: hubcapHeaders(apiKey), signal: AbortSignal.timeout(15000) },
    );
    if (response.ok) {
      const body: unknown = await response.json();
      if (body && typeof body === "object") {
        const record = body as Record<string, unknown>;
        if (typeof record.denuvo === "boolean") return record.denuvo;
        if (typeof record.drm_notice === "string") {
          return record.drm_notice.toLowerCase().includes("denuvo");
        }
        return false;
      }
    }
  } catch (error) {
    console.warn("Hubcap DRM check failed:", error);
  }

  try {
    const response = await fetch(
      `https://store.steampowered.com/api/appdetails?appids=${appId}`,
      { signal: AbortSignal.timeout(15000) },
    );
    if (response.ok) {
      const body = await response.json() as Record<string, {
        success?: boolean;
        data?: { drm_notice?: string };
      }>;
      const details = body[String(appId)];
      if (details?.success && details.data) {
        return (details.data.drm_notice ?? "").toLowerCase().includes("denuvo");
      }
    }
  } catch (error) {
    console.warn("Steam DRM check failed:", error);
  }

  try {
    const response = await fetch(
      "https://raw.githubusercontent.com/trionine/ManifestHub/main/data/denuvo-games.json",
      { signal: AbortSignal.timeout(15000) },
    );
    if (response.ok) {
      const appIds: unknown = await response.json();
      if (Array.isArray(appIds) && appIds.every((value) => Number.isInteger(value))) {
        return appIds.includes(appId);
      }
    }
  } catch (error) {
    console.warn("Static Denuvo list check failed:", error);
  }

  return null;
};

const downloadHubcapFiles = async (
  appId: number,
  isDenuvo: boolean,
  apiKey: string,
  hubcapBaseUrl: string,
) => {
  const files: UpstreamFile[] = [];
  const paths = [
    { path: `/lua/${appId}`, fallback: `${appId}.lua` },
    ...(isDenuvo ? [{ path: `/manifest/${appId}`, fallback: `${appId}.manifest` }] : []),
  ];

  for (const item of paths) {
    const response = await fetch(
      `${hubcapBaseUrl}${item.path}`,
      { headers: hubcapHeaders(apiKey), signal: AbortSignal.timeout(20000) },
    );
    if (response.status === 401 || response.status === 403) {
      return { accessDenied: true, rateLimited: false, files: [] as UpstreamFile[] };
    }
    if (response.status === 429) {
      return { accessDenied: false, rateLimited: true, files: [] as UpstreamFile[] };
    }
    if (response.status === 404) continue;
    if (!response.ok) {
      throw new Error(`Hubcap Manifest returned HTTP ${response.status}.`);
    }

    files.push({
      fileName: fileNameFromResponse(response, item.fallback),
      contentType: response.headers.get("content-type") ?? "application/octet-stream",
      content: new Uint8Array(await response.arrayBuffer()),
    });
  }

  return { accessDenied: false, rateLimited: false, files };
};

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
    console.error("Game asset function is missing Supabase configuration.");
    return jsonResponse(503, { error: "Game asset service is not configured." });
  }

  let body: { appId?: unknown; gameName?: unknown; deviceId?: unknown };
  try {
    body = await request.json();
  } catch {
    return jsonResponse(400, { error: "Invalid request body." });
  }

  const appId = typeof body.appId === "number" ? body.appId : 0;
  const gameName = typeof body.gameName === "string" ? body.gameName.trim() : "";
  const deviceId = typeof body.deviceId === "string" ? body.deviceId.trim() : "";
  if (!Number.isInteger(appId) || appId <= 0 ||
    gameName.length < 1 || gameName.length > 256 ||
    deviceId.length < 4 || deviceId.length > 256) {
    return jsonResponse(400, { error: "Invalid game request." });
  }

  const admin = createClient(supabaseUrl, serviceRoleKey, {
    auth: { autoRefreshToken: false, persistSession: false },
  });

  try {
    const { data: profile, error: profileError } = await admin
      .from("user_profiles")
      .select("device_id")
      .eq("device_id", deviceId)
      .maybeSingle();
    if (profileError) {
      console.error("Could not verify request device:", profileError.message);
      return jsonResponse(503, { error: "Could not verify request access." });
    }
    if (!profile) {
      return jsonResponse(403, { error: "A registered device profile is required." });
    }

    const { data: blockedDevice, error: blockedError } = await admin
      .from("blocked_devices")
      .select("device_id")
      .eq("device_id", deviceId)
      .limit(1)
      .maybeSingle();
    if (blockedError) {
      console.error("Could not verify request device block status:", blockedError.message);
      return jsonResponse(503, { error: "Could not verify request access." });
    }
    if (blockedDevice) {
      return jsonResponse(403, { error: "This device is not allowed to request game files." });
    }

    const hubcapApiKeys = await getHubcapApiKeys(admin);
    if (hubcapApiKeys.length === 0) {
      return errorResponse(
        401,
        "hubcap_api_key_invalid",
        "Hubcap Manifest",
        "No Hubcap API key is configured.",
      );
    }

    const hubcapBaseUrl = (Deno.env.get("HUBCAP_BASE_URL") ??
      "https://hubcapmanifest.com/api/v1").replace(/\/+$/, "");
    let hubcapApiKey: string | null = null;
    let isDenuvo: boolean | null = null;
    let files: UpstreamFile[] = [];
    let invalidKeyCount = 0;
    let rateLimited = false;
    let gameAccessDenied = false;
    let denuvoDetectionFailed = false;
    let selectedHubcapAccount: 1 | 2 | null = null;

    for (const { accountNumber, apiKey: candidateKey } of hubcapApiKeys) {
      let validationResponse: Response;
      try {
        validationResponse = await fetch(`${hubcapBaseUrl}/lua/730`, {
          headers: hubcapHeaders(candidateKey),
          signal: AbortSignal.timeout(15000),
        });
      } catch (error) {
        console.error("Could not validate a Hubcap API key:", error);
        return jsonResponse(502, { error: "Could not validate the Hubcap API keys." });
      }

      if (validationResponse.status === 401 || validationResponse.status === 403) {
        invalidKeyCount++;
        await validationResponse.body?.cancel();
        continue;
      }
      if (validationResponse.status === 429) {
        rateLimited = true;
        await validationResponse.body?.cancel();
        continue;
      }
      if (!validationResponse.ok) {
        console.error("Hubcap API key validation returned HTTP", validationResponse.status);
        await validationResponse.body?.cancel();
        return jsonResponse(502, { error: "Could not validate the Hubcap API keys." });
      }
      await validationResponse.body?.cancel();

      const candidateIsDenuvo = await detectDenuvo(appId, candidateKey);
      if (candidateIsDenuvo === null) {
        denuvoDetectionFailed = true;
        continue;
      }

      if (candidateIsDenuvo) {
        let result: Awaited<ReturnType<typeof downloadHubcapFiles>>;
        try {
          result = await downloadHubcapFiles(appId, candidateIsDenuvo, candidateKey, hubcapBaseUrl);
        } catch (error) {
          console.error("Could not download game files from Hubcap:", error);
          return jsonResponse(502, { error: "Could not download this game's files from Hubcap." });
        }

        if (result.accessDenied) {
          gameAccessDenied = true;
          continue;
        }
        if (result.rateLimited) {
          rateLimited = true;
          continue;
        }

        hubcapApiKey = candidateKey;
        isDenuvo = true;
        selectedHubcapAccount = accountNumber;
        files = result.files;
        break;
      }

      hubcapApiKey = candidateKey;
      isDenuvo = false;
      selectedHubcapAccount = accountNumber;
      break;
    }

    if (hubcapApiKey === null || isDenuvo === null) {
      if (invalidKeyCount === hubcapApiKeys.length) {
        return errorResponse(
          401,
          "hubcap_api_key_invalid",
          "Hubcap Manifest",
          "All configured Hubcap API keys are invalid or expired.",
        );
      }
      if (gameAccessDenied) {
        console.warn("Hubcap denied game-file access for all usable API keys.");
        return errorResponse(
          502,
          "hubcap_game_access_denied",
          "Hubcap Manifest",
          "Hubcap denied access to this game's files for all usable accounts.",
        );
      }
      if (rateLimited) {
        return jsonResponse(502, { error: "All configured Hubcap accounts are rate limited." });
      }
      if (denuvoDetectionFailed) {
        return jsonResponse(502, { error: "Could not determine whether the game uses Denuvo." });
      }
      return errorResponse(
        502,
        "hubcap_keys_unavailable",
        "Hubcap Manifest",
        "No configured Hubcap account could process this request.",
      );
    }

    let source = isDenuvo ? "Hubcap Manifest" : "lua.tools";

    if (isDenuvo) {
      console.log(`Hubcap request completed using account ${selectedHubcapAccount}.`);
    } else {
      const luaToolsCookie = await getCredential(admin, "lua_tools_cookie");
      if (!luaToolsCookie.trim()) {
        return errorResponse(
          401,
          "lua_tools_cookie_invalid",
          source,
          "lua.tools cookie is empty or invalid.",
        );
      }

      let cookieHeader: string;
      try {
        cookieHeader = luaToolsCookieHeader(luaToolsCookie);
      } catch (error) {
        console.error("Could not parse the stored lua.tools cookie:", error);
        return errorResponse(
          401,
          "lua_tools_cookie_format_invalid",
          source,
          "The cookie export has no active cookies matching lua.tools and its manifest download path.",
        );
      }

      const query = new URLSearchParams({
        appid: String(appId),
        source: "Luie",
        game_name: gameName,
      });
      const response = await fetch(
        `https://lua.tools/api/manifest/download?${query.toString()}`,
        {
          headers: {
            Cookie: cookieHeader,
            "User-Agent": "HZLuaManager/1.0",
          },
          signal: AbortSignal.timeout(30000),
        },
      );

      if (response.status === 401 || response.status === 403) {
        return errorResponse(
          401,
          "lua_tools_cookie_invalid",
          source,
          "lua.tools cookie is invalid or expired.",
        );
      }
      let luaToolsFileMissing = response.status === 404;
      if (!luaToolsFileMissing && !response.ok) {
        console.error("lua.tools returned HTTP", response.status);
        return jsonResponse(502, { error: `lua.tools returned HTTP ${response.status}.` });
      }

      if (!luaToolsFileMissing) {
        const content = new Uint8Array(await response.arrayBuffer());
        luaToolsFileMissing = content.length === 0;
        if (!luaToolsFileMissing) {
          files.push({
            fileName: fileNameFromResponse(response, `${appId}.lua`),
            contentType: response.headers.get("content-type") ?? "application/octet-stream",
            content,
          });
        }
      }

      if (luaToolsFileMissing) {
        let accessDeniedCount = 0;
        let rateLimited = false;
        let fallbackFailed = false;

        for (const { accountNumber, apiKey: candidateKey } of hubcapApiKeys) {
          let result: Awaited<ReturnType<typeof downloadHubcapFiles>>;
          try {
            result = await downloadHubcapFiles(appId, false, candidateKey, hubcapBaseUrl);
          } catch (error) {
            console.error("Could not check Hubcap fallback account:", error);
            fallbackFailed = true;
            continue;
          }

          const availableFiles = result.files.filter((file) => file.content.length > 0);
          if (availableFiles.length > 0) {
            files = availableFiles;
            source = "Hubcap Manifest";
            selectedHubcapAccount = accountNumber;
            break;
          }
          if (result.accessDenied) accessDeniedCount++;
          if (result.rateLimited) rateLimited = true;
        }

        if (files.length === 0) {
          if (rateLimited) {
            return jsonResponse(502, { error: "Hubcap fallback is rate limited." });
          }
          if (accessDeniedCount === hubcapApiKeys.length) {
            return errorResponse(
              502,
              "hubcap_game_access_denied",
              "Hubcap Manifest",
              "Hubcap denied access to this game's files for all configured accounts.",
            );
          }
          if (fallbackFailed || accessDeniedCount > 0) {
            return errorResponse(
              502,
              "hubcap_keys_unavailable",
              "Hubcap Manifest",
              "Could not check this game's files with all configured Hubcap accounts.",
            );
          }
          source = "lua.tools and Hubcap Manifest";
        } else {
          console.log(`Hubcap fallback completed using account ${selectedHubcapAccount}.`);
        }
      }
    }

    if (files.length === 0 || files.every((file) => file.content.length === 0)) {
      return errorResponse(404, "game_not_found", source, `Game was not found on ${source}.`);
    }

    return multipartResponse(
      files.filter((file) => file.content.length > 0),
      isDenuvo,
      source,
    );
  } catch (error) {
    console.error("Game asset request failed:", error);
    return jsonResponse(502, { error: "The game source could not be reached." });
  }
});
