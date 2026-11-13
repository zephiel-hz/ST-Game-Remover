import { createClient } from "npm:@supabase/supabase-js@2";

type DiscordForumChannel = {
  id: string;
  guild_id: string;
  type: number;
  available_tags?: Array<{ id: string; name: string }>;
};

type DiscordThread = {
  id: string;
  guild_id?: string;
  parent_id?: string;
  name?: string;
  owner_id?: string;
  message_count?: number;
  total_message_sent?: number;
  applied_tags?: string[];
  thread_metadata?: { archived?: boolean };
};

type DiscordThreadList = {
  threads?: DiscordThread[];
  has_more?: boolean;
};

type DiscordMessage = {
  id: string;
  content?: string;
  timestamp?: string;
  author?: { username?: string; global_name?: string };
  attachments?: Array<{
    filename?: string;
    content_type?: string;
    size?: number;
    url?: string;
  }>;
};

type ForumAttachment = {
  fileName: string;
  contentType: string;
  size: number;
  url: string;
};

type ForumPost = {
  id: string;
  title: string;
  content: string;
  author: string;
  createdAt: string;
  url: string;
  replyCount: number;
  archived: boolean;
  tags: string[];
  attachments: ForumAttachment[];
};

type ForumReply = {
  id: string;
  author: string;
  content: string;
  createdAt: string;
  attachments: ForumAttachment[];
};

class DiscordApiError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
  }
}

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

const isDiscordId = (value: string) => /^\d{17,20}$/.test(value);

const createdAtFromSnowflake = (id: string) => {
  try {
    const timestamp = Number((BigInt(id) >> 22n) + 1420070400000n);
    return new Date(timestamp).toISOString();
  } catch {
    return new Date(0).toISOString();
  }
};

const discordErrorMessage = (status: number) => {
  if (status === 401) return "Discord rejected the bot token.";
  if (status === 403) return "The bot cannot read the configured forum or its threads.";
  if (status === 404) return "The configured Discord server or forum channel was not found.";
  return `Discord returned HTTP ${status}.`;
};

const attachmentsFromMessage = (message: DiscordMessage): ForumAttachment[] => {
  const attachments: ForumAttachment[] = [];
  for (const attachment of message.attachments ?? []) {
    if (!attachment.url) continue;

    try {
      const url = new URL(attachment.url);
      if (url.protocol === "https:" &&
        (url.hostname === "cdn.discordapp.com" || url.hostname === "media.discordapp.net")) {
        attachments.push({
          fileName: attachment.filename ?? "attachment",
          contentType: attachment.content_type ?? "",
          size: attachment.size ?? 0,
          url: url.toString(),
        });
      }
    } catch {
      continue;
    }
  }
  return attachments;
};

Deno.serve(async (request: Request) => {
  if (request.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }

  if (request.method !== "POST") {
    return jsonResponse(405, { error: "Method not allowed." });
  }

  const botToken = Deno.env.get("DISCORD_BOT_TOKEN");
  const guildId = Deno.env.get("DISCORD_GUILD_ID")?.trim() ?? "";
  const forumChannelId = Deno.env.get("DISCORD_FORUM_CHANNEL_ID")?.trim() ?? "";
  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");

  if (!botToken || !isDiscordId(guildId) || !isDiscordId(forumChannelId) ||
    !supabaseUrl || !serviceRoleKey) {
    console.error("Discord forum function is missing valid server-side configuration.");
    return jsonResponse(503, {
      error: "Discord forum access is missing required server-side configuration.",
    });
  }

  let body: { deviceId?: unknown; threadId?: unknown; beforeMessageId?: unknown };
  try {
    body = await request.json();
  } catch {
    return jsonResponse(400, { error: "Invalid request body." });
  }

  const deviceId = typeof body.deviceId === "string" ? body.deviceId.trim() : "";
  if (deviceId.length < 4 || deviceId.length > 256) {
    return jsonResponse(400, { error: "Invalid device identity." });
  }

  const threadId = typeof body.threadId === "string" ? body.threadId.trim() : "";
  const beforeMessageId =
    typeof body.beforeMessageId === "string" ? body.beforeMessageId.trim() : "";
  const hasBeforeMessageId =
    body.beforeMessageId !== undefined && body.beforeMessageId !== null;
  if ((body.threadId !== undefined && !isDiscordId(threadId)) ||
    (hasBeforeMessageId && !isDiscordId(beforeMessageId)) ||
    (beforeMessageId && !threadId)) {
    return jsonResponse(400, { error: "Invalid forum thread or reply cursor." });
  }

  const admin = createClient(supabaseUrl, serviceRoleKey, {
    auth: { autoRefreshToken: false, persistSession: false },
  });

  const { data: blockedDevice, error: blockedDeviceError } = await admin
    .from("blocked_devices")
    .select("device_id")
    .eq("device_id", deviceId)
    .limit(1)
    .maybeSingle();

  if (blockedDeviceError) {
    console.error("Could not check the device block list:", blockedDeviceError.message);
    return jsonResponse(503, { error: "Could not verify device access." });
  }
  if (blockedDevice) {
    return jsonResponse(403, { error: "This device is not allowed to access the forum." });
  }

  const { data: directlyBoundToken, error: directTokenError } = await admin
    .from("device_tokens")
    .select("device_id")
    .eq("is_active", true)
    .eq("device_id", deviceId)
    .limit(1)
    .maybeSingle();

  if (directTokenError) {
    console.error("Could not verify the device token binding:", directTokenError.message);
    return jsonResponse(503, { error: "Could not verify device access." });
  }

  let arrayBoundToken = null;
  if (!directlyBoundToken) {
    const { data, error } = await admin
      .from("device_tokens")
      .select("device_ids")
      .eq("is_active", true)
      .contains("device_ids", [deviceId])
      .limit(1)
      .maybeSingle();

    if (error) {
      console.error("Could not verify the device token binding:", error.message);
      return jsonResponse(503, { error: "Could not verify device access." });
    }
    arrayBoundToken = data;
  }

  if (!directlyBoundToken && !arrayBoundToken) {
    return jsonResponse(403, { error: "An active token is required to access the forum." });
  }

  const discordGet = async <T>(path: string): Promise<T> => {
    const response = await fetch(`https://discord.com/api/v10${path}`, {
      method: "GET",
      headers: {
        Authorization: `Bot ${botToken}`,
        "User-Agent": "DiscordBot (https://github.com/zephiel-hz/ST-Game-Remover, 1.0)",
      },
    });

    if (!response.ok) {
      throw new DiscordApiError(response.status, discordErrorMessage(response.status));
    }

    return await response.json() as T;
  };

  try {
    const forum = await discordGet<DiscordForumChannel>(`/channels/${forumChannelId}`);
    if (forum.guild_id !== guildId || forum.type !== 15) {
      return jsonResponse(400, {
        error: "The configured channel is not a forum channel in the configured server.",
      });
    }

    if (threadId) {
      const thread = await discordGet<DiscordThread>(`/channels/${threadId}`);
      if (thread.parent_id !== forumChannelId || thread.guild_id !== guildId) {
        return jsonResponse(404, { error: "The requested thread is not in the configured forum." });
      }

      const query = new URLSearchParams({ limit: "100" });
      if (beforeMessageId) query.set("before", beforeMessageId);
      const messages = await discordGet<DiscordMessage[]>(
        `/channels/${threadId}/messages?${query.toString()}`,
      );
      const hasMore = messages.length === 100;
      const nextCursor = hasMore ? messages[messages.length - 1]?.id ?? null : null;
      const replies: ForumReply[] = messages
        .filter((message) => message.id !== threadId)
        .sort((left, right) => {
          const difference = BigInt(left.id) - BigInt(right.id);
          return difference > 0n ? 1 : difference < 0n ? -1 : 0;
        })
        .map((message) => ({
          id: message.id,
          author: message.author?.global_name || message.author?.username || "Unknown user",
          content: message.content ?? "",
          createdAt: message.timestamp ?? createdAtFromSnowflake(message.id),
          attachments: attachmentsFromMessage(message),
        }));

      return jsonResponse(200, {
        replies,
        hasMore,
        beforeMessageId: nextCursor,
      });
    }

    const [activeResult, archivedResult] = await Promise.all([
      discordGet<DiscordThreadList>(`/guilds/${guildId}/threads/active`),
      discordGet<DiscordThreadList>(
        `/channels/${forumChannelId}/threads/archived/public?limit=100`,
      ),
    ]);

    const uniqueThreads = new Map<string, DiscordThread>();
    for (const thread of [...(activeResult.threads ?? []), ...(archivedResult.threads ?? [])]) {
      if (thread.parent_id === forumChannelId) {
        uniqueThreads.set(thread.id, thread);
      }
    }

    const threads = [...uniqueThreads.values()]
      .sort((left, right) => {
        const difference = BigInt(right.id) - BigInt(left.id);
        return difference > 0n ? 1 : difference < 0n ? -1 : 0;
      })
      .slice(0, 50);
    const tagNames = new Map((forum.available_tags ?? []).map((tag) => [tag.id, tag.name]));
    const posts: ForumPost[] = [];

    for (let offset = 0; offset < threads.length; offset += 5) {
      const batch = threads.slice(offset, offset + 5);
      const batchPosts = await Promise.all(batch.map(async (thread): Promise<ForumPost> => {
        let message: DiscordMessage | null = null;
        try {
          message = await discordGet<DiscordMessage>(
            `/channels/${thread.id}/messages/${thread.id}`,
          );
        } catch (error) {
          console.warn(`Could not read the starter post for Discord thread ${thread.id}:`, error);
        }

        return {
          id: thread.id,
          title: thread.name?.trim() || "Untitled forum post",
          content: message?.content ?? "",
          author: message?.author?.global_name || message?.author?.username || "Unknown user",
          createdAt: message?.timestamp ?? createdAtFromSnowflake(thread.id),
          url: `https://discord.com/channels/${guildId}/${thread.id}`,
          replyCount: thread.message_count ?? Math.max(0, (thread.total_message_sent ?? 1) - 1),
          archived: thread.thread_metadata?.archived ?? false,
          tags: (thread.applied_tags ?? []).map((tagId) => tagNames.get(tagId) ?? tagId),
          attachments: message ? attachmentsFromMessage(message) : [],
        };
      }));

      posts.push(...batchPosts);
    }

    posts.sort((left, right) =>
      new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()
    );

    return jsonResponse(200, {
      posts,
      fetchedAt: new Date().toISOString(),
    });
  } catch (error) {
    if (error instanceof DiscordApiError) {
      console.error("Discord forum request failed:", error.message);
      return jsonResponse(error.status === 429 ? 429 : 502, {
        error: error.message,
      });
    }

    console.error("Discord forum request failed:", error);
    return jsonResponse(502, { error: "Could not load posts from Discord." });
  }
});
