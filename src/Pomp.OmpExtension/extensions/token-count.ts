import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { isPompHost } from "../lib/vs-state.ts";

/** Uses the stock model-aware local tokenizer when reachable; never sends the draft to a provider. */
export async function countDraft(text: string, model: any): Promise<{ tokens: number; method: string; estimated: boolean; encoding: string | null }> {
  if (!text.length) return { tokens: 0, method: "empty", estimated: false, encoding: null };
  try {
    const { Tokenizer } = await import("@oh-my-pi/pi-agent-core");
    const tokenizer = new Tokenizer(model);
    const count = tokenizer.checkTokenBudget(text, 0);
    if (Number.isSafeInteger(count.tokens) && count.tokens >= 0) {
      const native = tokenizer.encoding !== null && count.exact;
      return { tokens: count.tokens, method: native ? "catalog-native" : count.exact ? "fallback-native" : "byte-upperbound", estimated: !native, encoding: tokenizer.encoding == null ? null : String(tokenizer.encoding) };
    }
  } catch { /* Standalone runtimes may not expose the tokenizer module. */ }
  return { tokens: Math.ceil(Buffer.byteLength(text, "utf8") / 4), method: "byte-estimate", estimated: true, encoding: null };
}

export default function tokenCount(pi: ExtensionAPI): void {
  if (!isPompHost()) return;
  pi.registerCommand("pomp-count-tokens", {
    description: "POMP local draft token transport",
    handler: async (args, ctx) => {
      let request: { id: string; sessionId: string; modelId: string; provider: string; text: string };
      try {
        if (args.length > 1100000) return;
        request = JSON.parse(Buffer.from(args.trim(), "base64").toString("utf8"));
        if (!request || !/^[a-f0-9]{32}$/.test(request.id) || typeof request.text !== "string" || request.text.length > 128000) return;
        for (const key of ["sessionId", "modelId", "provider"] as const)
          if (typeof request[key] !== "string" || !request[key] || request[key].length > (key === "sessionId" ? 128 : 512)) return;
      } catch { return; }
      const respond = (value: object): void => ctx.ui.notify("pomp-count-result:" + Buffer.from(JSON.stringify({ id: request.id, ...value })).toString("base64"), "info");
      if (ctx.sessionManager.getSessionId() !== request.sessionId || ctx.model?.id !== request.modelId || ctx.model?.provider !== request.provider) {
        respond({ error: "context-changed" }); return;
      }
      const count = await countDraft(request.text, ctx.model);
      if (ctx.sessionManager.getSessionId() !== request.sessionId || ctx.model?.id !== request.modelId || ctx.model?.provider !== request.provider) {
        respond({ error: "context-changed" }); return;
      }
      respond({ ...count, sessionId: request.sessionId, modelId: request.modelId, provider: request.provider, scope: "draft-only", characters: request.text.length });
    },
  });
}
