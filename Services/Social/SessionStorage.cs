using System;

namespace Zink.Services.Social
{
    public class UserModel
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = "";
        public string Email { get; set; } = "";
    }
}