/*
UGS Cloud Code Script: VivoxToken

Simplified HS256 JWT minting for Vivox Access Tokens (VAT).
Required secrets:
- VIVOX_ISSUER
- VIVOX_TOKEN_KEY
*/

let nodeCrypto = null;
try {
  nodeCrypto = require("crypto");
} catch (_) {
  nodeCrypto = null;
}

const JOIN_ACTION_PREFIX = "join~";

function base64UrlEncode(value) {
  return Buffer.from(String(value), "utf8")
    .toString("base64")
    .replace(/=/g, "")
    .replace(/\+/g, "-")
    .replace(/\//g, "_");
}

function base64UrlDecode(value) {
  if (!value) return "";
  let normalized = String(value).replace(/-/g, "+").replace(/_/g, "/");
  const mod = normalized.length % 4;
  if (mod === 2) normalized += "==";
  else if (mod === 3) normalized += "=";
  else if (mod === 1) return "";
  return Buffer.from(normalized, "base64").toString("utf8");
}

function parseIssuerFromAccountUri(accountUri) {
  // Expected shape: sip:.<issuer>.<playerId>[.<envId>].@<domain>
  if (!accountUri || typeof accountUri !== "string") return "";
  const m = /^sip:\.([^.]+)\./.exec(accountUri);
  return m && m[1] ? m[1] : "";
}

async function signHs256(message, secret) {
  if (nodeCrypto && typeof nodeCrypto.createHmac === "function") {
    const signature = nodeCrypto.createHmac("sha256", secret).update(message, "utf8").digest("base64");
    return signature.replace(/=/g, "").replace(/\+/g, "-").replace(/\//g, "_");
  }

  if (globalThis.crypto && globalThis.crypto.subtle) {
    const enc = new TextEncoder();
    const key = await globalThis.crypto.subtle.importKey(
      "raw",
      enc.encode(secret),
      { name: "HMAC", hash: "SHA-256" },
      false,
      ["sign"]
    );
    const sig = await globalThis.crypto.subtle.sign("HMAC", key, enc.encode(message));
    return Buffer.from(new Uint8Array(sig))
      .toString("base64")
      .replace(/=/g, "")
      .replace(/\+/g, "-")
      .replace(/\//g, "_");
  }

  throw new Error("VivoxToken: no HS256-capable crypto API available in Cloud Code runtime.");
}

async function vxGenerateToken(secret, payload) {
  // Unity example style: base64url(header).base64url(payload).base64url(signature)
  const encodedHeader = base64UrlEncode(JSON.stringify({ typ: "JWT", alg: "HS256" }));
  const encodedPayload = base64UrlEncode(JSON.stringify(payload));
  const toSign = `${encodedHeader}.${encodedPayload}`;
  const encodedSignature = await signHs256(toSign, secret);
  return `${toSign}.${encodedSignature}`;
}

module.exports = async ({ params, secretManager }) => {
  if (!secretManager) {
    throw new Error("VivoxToken: secretManager is unavailable.");
  }

  const issuerSecret = await secretManager.getSecret("VIVOX_ISSUER");
  const issuerFromSecret = issuerSecret && issuerSecret.value ? String(issuerSecret.value).trim() : "";

  const tokenKeySecret = await secretManager.getSecret("VIVOX_TOKEN_KEY");
  const secret = tokenKeySecret && tokenKeySecret.value ? String(tokenKeySecret.value) : "";
  if (!secret) {
    throw new Error("VivoxToken: missing signing key (set secret VIVOX_TOKEN_KEY).");
  }

  const rawAction = params && params.action ? String(params.action) : "";
  const action = rawAction.startsWith(JOIN_ACTION_PREFIX) ? "join" : rawAction;
  const fromUserUri = params && params.fromUserUri ? String(params.fromUserUri) : "";
  let channelUri = params && params.channelUri ? String(params.channelUri) : "";
  const targetUserUri = params && params.targetUserUri ? String(params.targetUserUri) : "";

  if (!action) throw new Error("VivoxToken: missing params.action.");
  if (!fromUserUri) throw new Error("VivoxToken: missing params.fromUserUri.");

  if (!channelUri && rawAction.startsWith(JOIN_ACTION_PREFIX)) {
    channelUri = base64UrlDecode(rawAction.substring(JOIN_ACTION_PREFIX.length));
  }

  if (action === "join" && !channelUri) {
    throw new Error("VivoxToken: missing params.channelUri for join action.");
  }

  const issuer = parseIssuerFromAccountUri(fromUserUri) || issuerFromSecret;
  if (!issuer) {
    throw new Error("VivoxToken: missing issuer (set secret VIVOX_ISSUER).");
  }

  // Preserve existing behavior:
  // if caller passes a positive value, use it directly as exp; otherwise default to now + 90s.
  let exp = params && params.expiration ? Number(params.expiration) : 0;
  if (!Number.isFinite(exp) || exp <= 0) {
    exp = Math.floor(Date.now() / 1000) + 90;
  }

  const payload = {
    iss: issuer,
    exp,
    vxa: action,
    vxi: Math.floor(Math.random() * 2147483647),
    f: fromUserUri
  };

  if (channelUri) payload.t = channelUri;
  if (targetUserUri) payload.sub = targetUserUri;

  return await vxGenerateToken(secret, payload);
};
