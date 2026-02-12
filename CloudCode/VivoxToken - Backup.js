/*
UGS Cloud Code Script: VivoxToken

This script mints a Vivox Access Token (VAT) server-side so you can run with:
- Vivox Test Mode disabled
- No Vivox signing key shipped in the Unity client

Required secrets (set in UGS Secret Manager, per-environment):
- VIVOX_ISSUER      (your Vivox "token issuer" / title id)
- VIVOX_TOKEN_KEY   (your Vivox "token key" / signing secret)
*/

// NOTE: Cloud Code "scripts" run in a restricted JS runtime where Node's built-in `crypto`
// module is not available. We therefore implement the minimal HS256 (HMAC-SHA256) pieces
// in pure JS without imports.

const b64abc = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const JOIN_ACTION_PREFIX = "join~";

function utf8Bytes(str) {
  // Cloud Code scripts may not provide TextEncoder. Encode UTF-16 JS strings into UTF-8 bytes manually.
  const s = String(str);
  const out = [];
  for (let i = 0; i < s.length; i++) {
    let codePoint = s.codePointAt(i);
    if (codePoint > 0xffff) {
      i++;
    }

    if (codePoint <= 0x7f) {
      out.push(codePoint);
    } else if (codePoint <= 0x7ff) {
      out.push(0xc0 | (codePoint >>> 6));
      out.push(0x80 | (codePoint & 0x3f));
    } else if (codePoint <= 0xffff) {
      out.push(0xe0 | (codePoint >>> 12));
      out.push(0x80 | ((codePoint >>> 6) & 0x3f));
      out.push(0x80 | (codePoint & 0x3f));
    } else {
      out.push(0xf0 | (codePoint >>> 18));
      out.push(0x80 | ((codePoint >>> 12) & 0x3f));
      out.push(0x80 | ((codePoint >>> 6) & 0x3f));
      out.push(0x80 | (codePoint & 0x3f));
    }
  }
  return new Uint8Array(out);
}

function bytesToBase64(bytes) {
  let result = "";
  let i = 0;
  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i] << 16) | (bytes[i + 1] << 8) | (bytes[i + 2]);
    result += b64abc[(n >>> 18) & 63] + b64abc[(n >>> 12) & 63] + b64abc[(n >>> 6) & 63] + b64abc[n & 63];
  }
  if (i < bytes.length) {
    const a = bytes[i];
    const b = (i + 1 < bytes.length) ? bytes[i + 1] : 0;
    const n = (a << 16) | (b << 8);
    result += b64abc[(n >>> 18) & 63] + b64abc[(n >>> 12) & 63];
    result += (i + 1 < bytes.length) ? b64abc[(n >>> 6) & 63] : "=";
    result += "=";
  }
  return result;
}

function base64UrlFromBytes(bytes) {
  return bytesToBase64(bytes)
    .replace(/=/g, "")
    .replace(/\+/g, "-")
    .replace(/\//g, "_");
}

function base64UrlFromString(str) {
  return base64UrlFromBytes(utf8Bytes(str));
}

function rotr(x, n) {
  return (x >>> n) | (x << (32 - n));
}

function sha256(bytes) {
  const K = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
  ];

  let h0 = 0x6a09e667;
  let h1 = 0xbb67ae85;
  let h2 = 0x3c6ef372;
  let h3 = 0xa54ff53a;
  let h4 = 0x510e527f;
  let h5 = 0x9b05688c;
  let h6 = 0x1f83d9ab;
  let h7 = 0x5be0cd19;

  const l = bytes.length;
  const bitLenHi = Math.floor((l * 8) / 0x100000000);
  const bitLenLo = (l * 8) >>> 0;

  const withOne = l + 1;
  const padLen = (withOne % 64 <= 56) ? (56 - (withOne % 64)) : (56 + 64 - (withOne % 64));
  const totalLen = l + 1 + padLen + 8;
  const msg = new Uint8Array(totalLen);
  msg.set(bytes, 0);
  msg[l] = 0x80;
  msg[totalLen - 8] = (bitLenHi >>> 24) & 255;
  msg[totalLen - 7] = (bitLenHi >>> 16) & 255;
  msg[totalLen - 6] = (bitLenHi >>> 8) & 255;
  msg[totalLen - 5] = bitLenHi & 255;
  msg[totalLen - 4] = (bitLenLo >>> 24) & 255;
  msg[totalLen - 3] = (bitLenLo >>> 16) & 255;
  msg[totalLen - 2] = (bitLenLo >>> 8) & 255;
  msg[totalLen - 1] = bitLenLo & 255;

  const w = new Uint32Array(64);
  for (let offset = 0; offset < msg.length; offset += 64) {
    for (let i = 0; i < 16; i++) {
      const j = offset + i * 4;
      w[i] = ((msg[j] << 24) | (msg[j + 1] << 16) | (msg[j + 2] << 8) | (msg[j + 3])) >>> 0;
    }
    for (let i = 16; i < 64; i++) {
      const s0 = (rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3)) >>> 0;
      const s1 = (rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10)) >>> 0;
      w[i] = (w[i - 16] + s0 + w[i - 7] + s1) >>> 0;
    }

    let a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;

    for (let i = 0; i < 64; i++) {
      const S1 = (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) >>> 0;
      const ch = ((e & f) ^ (~e & g)) >>> 0;
      const temp1 = (h + S1 + ch + K[i] + w[i]) >>> 0;
      const S0 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) >>> 0;
      const maj = ((a & b) ^ (a & c) ^ (b & c)) >>> 0;
      const temp2 = (S0 + maj) >>> 0;

      h = g;
      g = f;
      f = e;
      e = (d + temp1) >>> 0;
      d = c;
      c = b;
      b = a;
      a = (temp1 + temp2) >>> 0;
    }

    h0 = (h0 + a) >>> 0;
    h1 = (h1 + b) >>> 0;
    h2 = (h2 + c) >>> 0;
    h3 = (h3 + d) >>> 0;
    h4 = (h4 + e) >>> 0;
    h5 = (h5 + f) >>> 0;
    h6 = (h6 + g) >>> 0;
    h7 = (h7 + h) >>> 0;
  }

  const out = new Uint8Array(32);
  const hs = [h0, h1, h2, h3, h4, h5, h6, h7];
  for (let i = 0; i < hs.length; i++) {
    out[i * 4] = (hs[i] >>> 24) & 255;
    out[i * 4 + 1] = (hs[i] >>> 16) & 255;
    out[i * 4 + 2] = (hs[i] >>> 8) & 255;
    out[i * 4 + 3] = hs[i] & 255;
  }
  return out;
}

function concatBytes(a, b) {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}

function hmacSha256(keyBytes, messageBytes) {
  const blockSize = 64;
  let key = keyBytes;
  if (key.length > blockSize) {
    key = sha256(key);
  }
  if (key.length < blockSize) {
    const k = new Uint8Array(blockSize);
    k.set(key, 0);
    key = k;
  }

  const oKeyPad = new Uint8Array(blockSize);
  const iKeyPad = new Uint8Array(blockSize);
  for (let i = 0; i < blockSize; i++) {
    oKeyPad[i] = key[i] ^ 0x5c;
    iKeyPad[i] = key[i] ^ 0x36;
  }

  const inner = sha256(concatBytes(iKeyPad, messageBytes));
  return sha256(concatBytes(oKeyPad, inner));
}

function signHs256(message, secret) {
  const sigBytes = hmacSha256(utf8Bytes(secret), utf8Bytes(message));
  return base64UrlFromBytes(sigBytes);
}

function parseIssuerFromAccountUri(accountUri) {
  // Expected: sip:.<issuer>.<playerId>[.<envId>].@<domain>
  if (!accountUri || typeof accountUri !== "string") return "";
  const m = /^sip:\.([^.]+)\./.exec(accountUri);
  return m && m[1] ? m[1] : "";
}

function decodeBase64UrlToString(value) {
  if (!value || typeof value !== "string") return "";
  let normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const mod = normalized.length % 4;
  if (mod === 2) normalized += "==";
  else if (mod === 3) normalized += "=";
  else if (mod === 1) return "";

  const decodeMap = {};
  for (let i = 0; i < b64abc.length; i++) {
    decodeMap[b64abc[i]] = i;
  }

  const bytes = [];
  for (let i = 0; i < normalized.length; i += 4) {
    const c0 = normalized[i];
    const c1 = normalized[i + 1];
    const c2 = normalized[i + 2];
    const c3 = normalized[i + 3];

    if (!c0 || !c1) return "";
    const n0 = decodeMap[c0];
    const n1 = decodeMap[c1];
    if (n0 == null || n1 == null) return "";

    const n2 = c2 === "=" ? 0 : decodeMap[c2];
    const n3 = c3 === "=" ? 0 : decodeMap[c3];
    if ((c2 !== "=" && n2 == null) || (c3 !== "=" && n3 == null)) return "";

    const triple = (n0 << 18) | (n1 << 12) | (n2 << 6) | n3;
    bytes.push((triple >>> 16) & 0xff);
    if (c2 !== "=") bytes.push((triple >>> 8) & 0xff);
    if (c3 !== "=") bytes.push(triple & 0xff);
  }

  let out = "";
  for (let i = 0; i < bytes.length; i++) {
    const b = bytes[i];
    if (b < 0x80) {
      out += String.fromCharCode(b);
    } else if ((b & 0xe0) === 0xc0 && i + 1 < bytes.length) {
      const cp = ((b & 0x1f) << 6) | (bytes[++i] & 0x3f);
      out += String.fromCharCode(cp);
    } else if ((b & 0xf0) === 0xe0 && i + 2 < bytes.length) {
      const cp = ((b & 0x0f) << 12) | ((bytes[++i] & 0x3f) << 6) | (bytes[++i] & 0x3f);
      out += String.fromCharCode(cp);
    } else {
      return "";
    }
  }

  return out;
}

module.exports = async ({ params, context, logger, secretManager }) => {
  if (!secretManager) {
    throw new Error("VivoxToken: secretManager is unavailable. Configure UGS Secret Manager integration for Cloud Code.");
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

  if (!action) {
    throw new Error("VivoxToken: missing params.action.");
  }

  if (!fromUserUri) {
    throw new Error("VivoxToken: missing params.fromUserUri.");
  }

  let channelUri = params && params.channelUri ? String(params.channelUri) : "";
  if (!channelUri && rawAction.startsWith(JOIN_ACTION_PREFIX)) {
    channelUri = decodeBase64UrlToString(rawAction.substring(JOIN_ACTION_PREFIX.length));
  }
  const targetUserUri = params && params.targetUserUri ? String(params.targetUserUri) : "";

  if (action === "join" && !channelUri) {
    throw new Error("VivoxToken: missing params.channelUri for join action.");
  }

  const issuer = parseIssuerFromAccountUri(fromUserUri) || issuerFromSecret;

  if (!issuer) {
    throw new Error("VivoxToken: missing issuer (set secret VIVOX_ISSUER).");
  }

  // If the client passed an absolute unix epoch expiration (seconds), use it.
  // Otherwise default to 90 seconds from now.
  let exp = 0;
  if (params && params.expiration) {
    exp = Number(params.expiration);
  }
  if (!Number.isFinite(exp) || exp <= 0) {
    exp = Math.floor(Date.now() / 1000) + 90;
  }

  // Vivox expects vxi as an integer nonce for uniqueness.
  // Keep this in signed 32-bit range to avoid claim parsing/validation issues.
  // Randomized nonce is sufficient for uniqueness in this stateless Cloud Code context.
  const vxi = Math.floor(Math.random() * 2147483647);

  const header = {
    typ: "JWT",
    alg: "HS256",
  };

  const payload = {
    iss: issuer,
    exp,
    vxa: action,
    vxi,
    f: fromUserUri,
  };

  if (channelUri) {
    payload.t = channelUri;
  }

  if (targetUserUri) {
    payload.sub = targetUserUri;
  }

  const encodedHeader = base64UrlFromString(JSON.stringify(header));
  const encodedPayload = base64UrlFromString(JSON.stringify(payload));
  const signingInput = `${encodedHeader}.${encodedPayload}`;
  const signature = signHs256(signingInput, secret);

  return `${signingInput}.${signature}`;
};

