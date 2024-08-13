﻿// <auto-generated />
using BTCPayServer.Plugins.BigCommercePlugin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.BigCommercePlugin.Migrations
{
    [DbContext(typeof(ShopifyDbContext))]
    partial class BigCommerceDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasDefaultSchema("BTCPayServer.Plugins.BigCommerce")
                .HasAnnotation("ProductVersion", "8.0.6")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("BTCPayServer.Plugins.BigCommercePlugin.Data.BigCommerceStore", b =>
                {
                    b.Property<string>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("text");

                    b.Property<string>("AccessToken")
                        .HasColumnType("text");

                    b.Property<string>("ApplicationUserId")
                        .HasColumnType("text");

                    b.Property<string>("BigCommerceUserEmail")
                        .HasColumnType("text");

                    b.Property<string>("BigCommerceUserId")
                        .HasColumnType("text");

                    b.Property<string>("ClientId")
                        .HasColumnType("text");

                    b.Property<string>("ClientSecret")
                        .HasColumnType("text");

                    b.Property<string>("JsFileUuid")
                        .HasColumnType("text");

                    b.Property<string>("RedirectUrl")
                        .HasColumnType("text");

                    b.Property<string>("Scope")
                        .HasColumnType("text");

                    b.Property<string>("StoreHash")
                        .HasColumnType("text");

                    b.Property<string>("StoreId")
                        .HasColumnType("text");

                    b.Property<string>("StoreName")
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.ToTable("BigCommerceStores", "BTCPayServer.Plugins.BigCommerce");
                });

            modelBuilder.Entity("BTCPayServer.Plugins.BigCommercePlugin.Data.Transaction", b =>
                {
                    b.Property<string>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("text");

                    b.Property<string>("ClientId")
                        .HasColumnType("text");

                    b.Property<string>("InvoiceId")
                        .HasColumnType("text");

                    b.Property<int>("InvoiceStatus")
                        .HasColumnType("integer");

                    b.Property<string>("OrderId")
                        .HasColumnType("text");

                    b.Property<string>("StoreHash")
                        .HasColumnType("text");

                    b.Property<string>("StoreId")
                        .HasColumnType("text");

                    b.Property<int>("TransactionStatus")
                        .HasColumnType("integer");

                    b.HasKey("Id");

                    b.ToTable("Transactions", "BTCPayServer.Plugins.BigCommerce");
                });
#pragma warning restore 612, 618
        }
    }
}
