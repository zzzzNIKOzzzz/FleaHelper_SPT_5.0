#nullable disable
using System;
using System.Collections.Generic;
using EFT;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem;

namespace FleaHelper
{
    public class PostPriceData
    {
        public string TemplateId { get; set; }
        public float Price { get; set; }

        public List<string> Items { get; set; } = new();
        public Dictionary<string, int> Requirements { get; set; } = new();

        public Il2CppStringArray Il2CppItems { get; set; }
        public Il2CppReferenceArray<BarterTemplate> Il2CppRequirements { get; set; }
    }
}
