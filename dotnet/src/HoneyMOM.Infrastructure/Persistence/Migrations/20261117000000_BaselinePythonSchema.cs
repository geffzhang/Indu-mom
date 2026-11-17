using HoneyMOM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HoneyMOM.Infrastructure.Persistence.Migrations;

[DbContext(typeof(HoneyMomDbContext))]
[Migration("20261117000000_BaselinePythonSchema")]
public sealed class BaselinePythonSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
