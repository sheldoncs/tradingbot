using System;
using System.ComponentModel.DataAnnotations;

namespace MetaTrader.Data.Entities
{
    public class GlobalTradeLock
    {
        [Key]
        public int Id { get; set; }
        public bool IsLocked { get; set; }
        public DateTime? LockedAt { get; set; }
    }
}
