using System.Collections.Generic;
using ABCRetailers.Models;
using Microsoft.EntityFrameworkCore;

namespace ABCRetailers.Data
{
    public class AuthDbContext : DbContext
    {
        public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options)
        {
        }

        // DbSets for both controllers
        public DbSet<User> Users { get; set; }
        public DbSet<Cart> Cart { get; set; }
    }
}
