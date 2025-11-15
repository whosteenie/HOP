using System;
using UnityEngine;

namespace KINEMATION.ScriptableWidget.Runtime
{
    [AttributeUsage(AttributeTargets.Class)]
    public class ScriptableComponentGroupAttribute : PropertyAttribute
    {
        public string group;
        public string shortName;

        public ScriptableComponentGroupAttribute(string group, string shortName)
        {
            this.group = group;
            this.shortName = shortName;
        }
    }
}