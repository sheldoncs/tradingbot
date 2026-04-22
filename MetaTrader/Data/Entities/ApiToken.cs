using System;
using System.ComponentModel.DataAnnotations;

namespace MetaTrader.Data.Entities
{
    public class ApiToken
    {
        [Key]
        public int id { get; set; }
        public string token { get; set; }
        public long account { get; set; }
        public bool isactive { get; set; }
        public DateTime createdat { get; set; }
    }

}
