﻿using System;

namespace RX_Explorer.Class
{
    public class TerminalProfile : IEquatable<TerminalProfile>
    {
        public string Name { get; set; }

        public string Argument { get; set; }

        public string Path { get; set; }

        public bool RunAsAdmin { get; set; }

        public TerminalProfile(string Name, string Path, string Argument, bool RunAsAdmin)
        {
            this.Name = Name;
            this.Path = Path;
            this.Argument = Argument;
            this.RunAsAdmin = RunAsAdmin;
        }

        public bool Equals(TerminalProfile other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            else
            {
                if (other == null)
                {
                    return false;
                }
                else
                {
                    return other.Name == Name;
                }
            }
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            else
            {
                if (obj is TerminalProfile Item)
                {
                    return Item.Name == Name;
                }
                else
                {
                    return false;
                }
            }
        }

        public override int GetHashCode()
        {
            return Name.GetHashCode();
        }

        public override string ToString()
        {
            return $"ProfileName: {Name}, Path: {Path}, RunAsAdmin: {RunAsAdmin}";
        }

        public static bool operator ==(TerminalProfile left, TerminalProfile right)
        {
            if (left is null)
            {
                return right is null;
            }
            else
            {
                if (right is null)
                {
                    return false;
                }
                else
                {
                    return left.Name == right.Name;
                }
            }
        }

        public static bool operator !=(TerminalProfile left, TerminalProfile right)
        {
            if (left is null)
            {
                return right is object;
            }
            else
            {
                if (right is null)
                {
                    return true;
                }
                else
                {
                    return left.Name != right.Name;
                }
            }
        }
    }
}
