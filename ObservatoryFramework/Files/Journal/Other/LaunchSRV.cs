﻿namespace Observatory.Framework.Files.Journal
{
    public class LaunchSRV : JournalBase
    {
        public string Loadout { get; init; }
        public ulong ID { get; init; }
        public bool PlayerControlled { get; init; }
    }
}
