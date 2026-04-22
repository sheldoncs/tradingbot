using System;
using System.ComponentModel.DataAnnotations;

namespace MetaTrader.Data.Entities
{
    public class Diagnostic
    {
        [Key]
        public int id { get; set; }
        public string symbol { get; set; }
        public string reason { get; set; }
        public DateTime timerecorded { get; set; }
    }
}
