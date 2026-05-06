const assert = require("node:assert/strict");
const { main } = require("./index");

assert.equal(main(), "fixture");
