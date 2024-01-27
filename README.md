# This repository has moved

Please find our new home at Velopack: https://github.com/velopack/velopack

<a href="https://github.com/velopack/velopack">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/velopack/velopack/master/docfx/images/velopack-white.svg">
  <img alt="Velopack Logo" src="https://raw.githubusercontent.com/velopack/velopack/master/docfx/images/velopack-black.svg" width="300">
</picture>
</a>

## What does this mean for me?
Well, Velopack has been designed to seamlessly migrate your app from Squirrel.Windows or Clowd.Squirrel and will be very familiar for you. If you are already a user of Clowd.Squirrel, the command line interface is pretty much the same! Change to the Velopack NuGet package, make some minor tweaks to your app startup, and release your next update at ⚡ lightning speed.

## Why the rebrand?
This repository started out as a humble fork of Squirrel.Windows, which was an epic library but has currently fallen into a state of disrepair. I started this fork in 2021, and since then I have had to write several large architectural reworks to solve fudemental problems present in the original library. There was such little of the original Squirrel.Windows code remaining. During the development of Clowd.Squirrel V3, I ended up discarding all that remained to re-write the core Squirrel binaries in Rust. In doing so, I've been able to make a framework which is faster, smaller, and more reliable. Though, because it's so different, I decided it was time to re-brand and become something new. With this re-branding, I have so many new features and plans in mind for the future - so I hope you make the switch and stay tuned.

## What if I just wanted Squirrel.Windows with some basic bug fixes?
For the time being, I'll still endevour to support some very basic bug fixes to Clowd.Squirrel V2 as they arise - which is architecturally very similar to the original Squirrel.Windows. You can [check out the master branch](https://github.com/clowd/Clowd.Squirrel/tree/master) for that. In the future, as more people move to Velopack, I may eventually archive this repository.
