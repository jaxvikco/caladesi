namespace Caladesi.Compiler.Tests.Emit;

[TestClass]
public class EmitTypesTests
{
    private static void Check(string source, string expected)
    {
        (string? output, int _) = EmitHarness.Run(source);
        Assert.AreEqual(expected, output);
    }

    [TestMethod]
    public void ClassBasics()
    {
        Check("""
        class Animal {
          name: string;
          constructor(name: string) { this.name = name; }
          speak(): string { return `${this.name} makes a sound`; }
        }
        class Dog extends Animal {
          override speak(): string { return `${this.name} barks`; }
        }
        const a: Animal = new Dog("Rex");
        Console.WriteLine(a.speak());
        """, "Rex barks\n");
    }

    [TestMethod]
    public void ParameterPropertiesAndAccessors()
    {
        Check("""
        class Counter {
          private _n = 0;
          constructor(public readonly step: int) {}
          get value(): int { return this._n; }
          inc(): void { this._n += this.step; }
          static create(): Counter { return new Counter(2); }
        }
        const c = Counter.create();
        c.inc(); c.inc();
        Console.WriteLine(c.value);
        """, "4\n");
    }

    [TestMethod]
    public void Interfaces()
    {
        Check("""
        interface Shape { area(): double; }
        class Sq implements Shape {
          constructor(private s: double) {}
          area(): double { return this.s * this.s; }
        }
        const s: Shape = new Sq(3);
        Console.WriteLine(s.area());
        """, "9\n");
    }

    [TestMethod]
    public void Records()
    {
        Check("""
        type Point = { x: int; y: int };
        const p = new Point(1, 2);
        const q: Point = { x: 1, y: 2 };
        Console.WriteLine(p == q);
        Console.WriteLine(p);
        const r = { ...p, x: 5 };
        Console.WriteLine(r.x + r.y);
        """, "True\nPoint { x = 1, y = 2 }\n7\n");
    }

    [TestMethod]
    public void Enums()
    {
        Check("""
        enum Color { Red, Green = 5, Blue }
        const c = Color.Blue;
        Console.WriteLine(c);
        Console.WriteLine(c as int);
        """, "Blue\n6\n");
    }

    [TestMethod]
    public void StringLiteralUnionEnum()
    {
        Check("""
        type Dir = "up" | "down";
        function f(d: Dir): int { return d == "up" ? 1 : -1; }
        Console.WriteLine(f("up"));
        Console.WriteLine(f("down"));
        """, "1\n-1\n");
    }

    [TestMethod]
    public void UnionsAndNarrowing()
    {
        Check("""
        class Circle { constructor(public r: double) {} }
        class Square { constructor(public s: double) {} }
        type Shape = Circle | Square;
        function area(s: Shape): double {
          if (s instanceof Circle) return 3 * s.r * s.r;
          return s.s * s.s;
        }
        Console.WriteLine(area(new Circle(2)));
        Console.WriteLine(area(new Square(3)));
        """, "12\n9\n");
    }

    [TestMethod]
    public void GenericFunctionAndClass()
    {
        Check("""
        function first<T>(xs: T[]): T { return xs[0]; }
        class Box<T> { constructor(public value: T) {} get(): T { return this.value; } }
        Console.WriteLine(first([5, 6]));
        Console.WriteLine(new Box<string>("hi").get());
        """, "5\nhi\n");
    }

    [TestMethod]
    public void Collections()
    {
        Check("""
        const list = new List<int>();
        list.Add(1); list.Add(2);
        const d = new Dictionary<string, int>();
        d["a"] = 1;
        Console.WriteLine(list.Count + d["a"]);
        const m: int[] = [...list, 9];
        Console.WriteLine(m.Length);
        """, "3\n3\n");
    }

    [TestMethod]
    public void Destructuring()
    {
        Check("""
        const [a, b] = [1, 2];
        const t: [int, string] = [1, "x"];
        const [n, s] = t;
        Console.WriteLine(a + b);
        Console.WriteLine(`${n}${s}`);
        """, "3\n1x\n");
    }
}
