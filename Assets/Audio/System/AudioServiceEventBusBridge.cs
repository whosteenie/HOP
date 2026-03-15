using Events;
using Game.Audio2;
using Game.Player;
using Game.Player.Core;
using UnityEngine;

namespace Game.Audio2 {
    [DisallowMultipleComponent]
    public sealed class AudioServiceEventBusBridge : MonoBehaviour {
        private void OnEnable() {
            EventBus.Subscribe<PlayLocalSoundIdEvent>(OnPlayLocal);
            EventBus.Subscribe<PlayLocalWorldSoundIdEvent>(OnPlayLocalWorld);
            EventBus.Subscribe<PlayLocalAttachedSoundIdEvent>(OnPlayLocalAttached);
            EventBus.Subscribe<StopLocalSoundIdEvent>(OnStopLocal);
            EventBus.Subscribe<StopAllLocalSoundsEvent>(OnStopAllLocal);

            EventBus.Subscribe<RequestNetworkWorldSoundIdEvent>(OnRequestNetworkWorld);
            EventBus.Subscribe<RequestNetworkAttachedSoundIdEvent>(OnRequestNetworkAttached);
        }

        private void OnDisable() {
            EventBus.Unsubscribe<PlayLocalSoundIdEvent>(OnPlayLocal);
            EventBus.Unsubscribe<PlayLocalWorldSoundIdEvent>(OnPlayLocalWorld);
            EventBus.Unsubscribe<PlayLocalAttachedSoundIdEvent>(OnPlayLocalAttached);
            EventBus.Unsubscribe<StopLocalSoundIdEvent>(OnStopLocal);
            EventBus.Unsubscribe<StopAllLocalSoundsEvent>(OnStopAllLocal);

            EventBus.Unsubscribe<RequestNetworkWorldSoundIdEvent>(OnRequestNetworkWorld);
            EventBus.Unsubscribe<RequestNetworkAttachedSoundIdEvent>(OnRequestNetworkAttached);
        }

        private static void OnPlayLocal(PlayLocalSoundIdEvent evt) {
            if(evt == null) return;
            if(AudioService.Instance == null) return;
            if(string.IsNullOrWhiteSpace(evt.SoundId)) return;
            AudioService.Instance.Play(evt.SoundId, Vector3.zero);
        }

        private static void OnPlayLocalWorld(PlayLocalWorldSoundIdEvent evt) {
            if(evt == null) return;
            if(AudioService.Instance == null) return;
            if(string.IsNullOrWhiteSpace(evt.SoundId)) return;

            if(!evt.AllowOverlap) {
                AudioService.Instance.Stop(evt.SoundId);
            }
            AudioService.Instance.Play(evt.SoundId, evt.Position);
        }

        private static void OnPlayLocalAttached(PlayLocalAttachedSoundIdEvent evt) {
            if(evt == null) return;
            if(AudioService.Instance == null) return;
            if(string.IsNullOrWhiteSpace(evt.SoundId)) return;
            if(evt.Parent == null) return;

            if(!evt.AllowOverlap) {
                AudioService.Instance.Stop(evt.SoundId);
            }
            AudioService.Instance.PlayAttached(evt.SoundId, evt.Parent);
        }

        private static void OnStopLocal(StopLocalSoundIdEvent evt) {
            if(evt == null) return;
            if(AudioService.Instance == null) return;
            if(string.IsNullOrWhiteSpace(evt.SoundId)) return;
            AudioService.Instance.Stop(evt.SoundId);
        }

        private static void OnStopAllLocal(StopAllLocalSoundsEvent evt) {
            if(AudioService.Instance == null) return;
            AudioService.Instance.StopAll();
        }

        private static void OnRequestNetworkWorld(RequestNetworkWorldSoundIdEvent evt) {
            if(evt == null) return;
            if(string.IsNullOrWhiteSpace(evt.SoundId)) return;

            var local = PlayerController.LocalPlayer;
            if(local == null) return;
            var relay = local.AudioRelay;
            if(relay == null) return;

            relay.RequestPlay(evt.SoundId, evt.Position, evt.AllowOverlap, evt.Seed);
        }

        private static void OnRequestNetworkAttached(RequestNetworkAttachedSoundIdEvent evt) {
            if(evt == null) return;
            if(string.IsNullOrWhiteSpace(evt.SoundId)) return;

            var local = PlayerController.LocalPlayer;
            if(local == null) return;
            var relay = local.AudioRelay;
            if(relay == null) return;

            relay.RequestPlayAttached(evt.SoundId, evt.AttachTo, evt.AllowOverlap, evt.Seed);
        }
    }
}

