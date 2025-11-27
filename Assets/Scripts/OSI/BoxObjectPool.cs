using System.Collections.Generic;
using UnityEngine;

namespace OSI {
    public class BoxObjectPool : MonoBehaviour {
        public static BoxObjectPool Current;

        [Tooltip("Assign the box prefab.")] public Indicator pooledObject;
        [Tooltip("Initial pooled amount.")] public int pooledAmount = 1;
        [Tooltip("Should the pooled amount increase.")] public bool willGrow = true;

        private List<Indicator> _pooledObjects;

        private void Awake() {
            Current = this;
        }

        private void Start() {
            _pooledObjects = new List<Indicator>();

            for(var i = 0; i < pooledAmount; i++) {
                var box = Instantiate(pooledObject, transform, false);
                box.Activate(false);
                _pooledObjects.Add(box);
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
            var box = Instantiate(pooledObject, transform, false);
            box.Activate(false);
            _pooledObjects.Add(box);
            return box;
        }

        /// <summary>
        /// Deactivate all the objects in the pool.
        /// </summary>
        public void DeactivateAllPooledObjects() {
            foreach(var box in _pooledObjects) {
                box.Activate(false);
            }
        }
    }
}