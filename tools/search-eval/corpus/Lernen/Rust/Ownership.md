# Rust ownership

## Ownership

Every value has one owner; when the owner goes out of scope, the value is dropped. Assignment moves ownership unless the type is Copy.

## Borrowing

References borrow without taking ownership. Any number of shared references, or exactly one mutable reference, never both at once: the borrow checker enforces this at compile time.

## Shared ownership

Rc for several owners in one thread, Arc across threads, RefCell to mutate behind a shared reference with checks at run time.
