using MetaTrader.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaTrader.Util
{
    public static class TokenStore
    {
        private static readonly List<ApiToken> _tokens = new()
    {
        new ApiToken
        {
            token = "1a60ccf7-2457-455b-958e-9459e9e82d62",
            account = 12345678,
            isactive = true,
            createdat = DateTime.UtcNow
        }
    };

        public static ApiToken Get(string token)
        {
            return _tokens.FirstOrDefault(t => t.token == token);
        }
    }

}
