using UnityEngine;

namespace Game.Social {
    public static class PlayerIconPicker {
        // These IDs map to USS classes: .player-icon-<id>
        public const string Red = "red";
        public const string Orange = "orange";
        public const string Yellow = "yellow";
        public const string Green = "green";
        public const string Blue = "blue";
        public const string Purple = "purple";
        public const string White = "white";

        private struct Candidate {
            public string Id;
            public Color Color;
        }

        private static readonly Candidate[] Candidates = {
            new Candidate { Id = Red, Color = new Color(1f, 0.25f, 0.25f, 1f) },
            new Candidate { Id = Orange, Color = new Color(1f, 0.55f, 0.15f, 1f) },
            new Candidate { Id = Yellow, Color = new Color(1f, 0.9f, 0.2f, 1f) },
            new Candidate { Id = Green, Color = new Color(0.2f, 1f, 0.45f, 1f) },
            new Candidate { Id = Blue, Color = new Color(0.25f, 0.6f, 1f, 1f) },
            new Candidate { Id = Purple, Color = new Color(0.7f, 0.35f, 1f, 1f) },
            new Candidate { Id = White, Color = new Color(0.92f, 0.92f, 0.92f, 1f) }
        };

        public static string PickIconIdFromBaseColor(Vector4 baseColor, bool hide) {
            if(hide) return White;

            var c = new Color(baseColor.x, baseColor.y, baseColor.z, 1f);

            var bestId = White;
            var bestDist = float.MaxValue;

            for(var i = 0; i < Candidates.Length; i++) {
                var cand = Candidates[i];
                var d = ColorDistanceSqr(c, cand.Color);
                if(d < bestDist) {
                    bestDist = d;
                    bestId = cand.Id;
                }
            }

            return bestId;
        }

        public static string PickDeterministicIconId(ulong seed, bool hide) {
            if(hide) return White;
            var index = (int)(seed % (ulong)Candidates.Length);
            return Candidates[index].Id;
        }

        private static float ColorDistanceSqr(Color a, Color b) {
            var dr = a.r - b.r;
            var dg = a.g - b.g;
            var db = a.b - b.b;
            return dr * dr + dg * dg + db * db;
        }
    }
}

