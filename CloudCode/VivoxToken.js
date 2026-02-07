/*
UGS Cloud Code Script: VivoxToken

This script mints a Vivox Access Token (VAT) server-side so you can run with:
- Vivox Test Mode disabled
- No Vivox signing key shipped in the Unity client

Required environment variables (set in the UGS dashboard per-environment):
- VIVOX_ISSUER      (your Vivox "token issuer" / title id)
- VIVOX_TOKEN_KEY   (your Vivox "token key" / signing secret)
*/

const crypto = require("crypto");

function base64UrlEncode(input) {
  return Buffer.from(input)
    .toString("base64")
    .replace(/=/g, "")
    .replace(/\+/g, "-")
    .replace(/\//g, "_");
}

function signHs256(message, secret) {
  const hmac = crypto.createHmac("sha256", secret);
  hmac.update(message);
  return base64UrlEncode(hmac.digest());
}

module.exports = async ({ params, context, logger }) => {
  const issuer = (params && params.issuer) ? params.issuer : process.env.VIVOX_ISSUER;
  const secret = process.env.VIVOX_TOKEN_KEY;

  if (!issuer) {
    throw new Error("VivoxToken: missing issuer (set VIVOX_ISSUER or pass params.issuer).");
  }

  if (!secret) {
    throw new Error("VivoxToken: missing signing key (set VIVOX_TOKEN_KEY).");
  }

  const action = params && params.action ? String(params.action) : "";
  const fromUserUri = params && params.fromUserUri ? String(params.fromUserUri) : "";

  if (!action) {
    throw new Error("VivoxToken: missing params.action.");
  }

  if (!fromUserUri) {
    throw new Error("VivoxToken: missing params.fromUserUri.");
  }

  const channelUri = params && params.channelUri ? String(params.channelUri) : undefined;
  const targetUserUri = params && params.targetUserUri ? String(params.targetUserUri) : undefined;

  // If the client passed an absolute unix epoch expiration (seconds), use it.
  // Otherwise default to 90 seconds from now.
  let exp = 0;
  if (params && params.expiration) {
    exp = Number(params.expiration);
  }
  if (!Number.isFinite(exp) || exp <= 0) {
    exp = Math.floor(Date.now() / 1000) + 90;
  }

  // vxi must change whenever other claims are identical, so use a high-resolution timestamp.
  const vxi = Math.floor(Date.now());

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

  // Only include "t" when relevant (join/transcription/etc).
  if (channelUri) {
    payload.t = channelUri;
  }

  // Only include "sub" when relevant (mute/kick/block/etc).
  if (targetUserUri) {
    payload.sub = targetUserUri;
  }

  const encodedHeader = base64UrlEncode(JSON.stringify(header));
  const encodedPayload = base64UrlEncode(JSON.stringify(payload));
  const signingInput = `${encodedHeader}.${encodedPayload}`;
  const signature = signHs256(signingInput, secret);

  // Final VAT token string.
  return `${signingInput}.${signature}`;
};

