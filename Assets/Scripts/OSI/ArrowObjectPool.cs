using System.Collections.Generic;
using UnityEngine;

namespace OSI {
    internal class ArrowObjectPool : MonoBehaviour {
        public static ArrowObjectPool Current;

        [Tooltip("Assign the arrow prefab.")] public Indicator pooledObject;
        [Tooltip("Initial pooled amount.")] public int pooledAmount = 1;
        [Tooltip("Should the pooled amount increase.")] public bool willGrow = true;

        private List<Indicator> _pooledObjects;

        private void Awake() {
            Current = this;
        }

        private void Start() {
            _pooledObjects = new List<Indicator>();

            for(var i = 0; i < pooledAmount; i++) {
                var arrow = Instantiate(pooledObject, transform, false);
                arrow.Activate(false);
                _pooledObjects.Add(arrow);
            }
        }

        /// <summary>
        /// Gets pooled objects from the pool.
        /// </summary>
        /// <returns></returns>
        public Indicator GetPooledObject() {
            foreach(var t in _pooledObjects) {
                if(!t.Active) {
                    return t;
                }
            }

            if(!willGrow) return null;
            
            var arrow = Instantiate(pooledObject, transform, false);
            arrow.Activate(false);
            _pooledObjects.Add(arrow);
            return arrow;
        }

        /// <summary>
        /// Deactivate all the objects in the pool.
        /// </summary>
        public void DeactivateAllPooledObjects() {
            foreach(var arrow in _pooledObjects) {
                arrow.Activate(false);
            }
        }
    }
}